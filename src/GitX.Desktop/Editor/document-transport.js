// The last accepted disk body is separate from view metadata and writable
// Monaco models. This keeps one body only, never a cache of tab contents.
export class DocumentTransport {
  constructor(fetcher = (...args) => fetch(...args)) { this.fetcher = fetcher; this.content = null; }
  clear() { this.content = null; }
  async read(version, { signal, isCurrent = () => true } = {}) {
    const current = payload => !signal?.aborted && isCurrent() && payload?.version === version;
    const request = async known => {
      const suffix = known ? `&content=${encodeURIComponent(known)}` : '';
      const response = await this.fetcher(`document?v=${version}${suffix}`, { cache: 'no-store', signal });
      if (!response.ok) throw new Error('无法读取编辑器文档');
      return response.json();
    };
    let payload = await request(this.content?.id);
    if (!current(payload)) return null;
    if (payload.reuseContent === true && (!this.content || payload.contentId !== this.content.id)) {
      // A reload or overlapping load can lose the advertised body. Retry once
      // without a hint, still checking the publication generation afterwards.
      payload = await request(null);
      if (!current(payload)) return null;
      if (payload.reuseContent === true) throw new Error('编辑器正文不可用，请刷新后重试。');
    }
    const doc = payload.document;
    if (!doc || typeof doc.text !== 'string' || payload.reuseContent === true && (doc.isNotice || doc.isDiff || doc.diff))
      throw new Error('编辑器文档格式无效');
    const text = payload.reuseContent === true ? this.content.text : doc.text;
    this.content = typeof payload.contentId === 'string' && payload.contentId.length > 0 && !doc.isNotice && !doc.isDiff && !doc.diff
      ? { id: payload.contentId, text } : null;
    return { ...payload, document: { ...doc, text } };
  }
}
