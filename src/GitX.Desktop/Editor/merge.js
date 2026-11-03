import './node_modules/monaco-editor/esm/vs/nls/lang/zh-cn.js';
import * as monaco from 'monaco-editor/editor/editor.api.js';
import { installNativeBridge } from './native-bridge.js';
import './node_modules/monaco-editor/esm/vs/base/browser/ui/codicons/codicon/codicon.css';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import { prepareLanguage } from './language-definitions.js';
import { languageForPath } from './language-info.js';
import { threeWayRows, alignmentZones, lineCount, sourceLines } from './merge-layout.js';
import { editorTheme } from './editor-theme.js';
import { editorTypography } from './editor-typography.js';
import './idea-font.css';
import { initialChoice, resolvedChoice, canApplyAutomatically, decide } from './merge-choices.js';
import './find-widget.css';
import { MergeSaveState } from './merge-save-state.js';
import { installFocusBoundary } from './focus-boundary.js';
import { iconLabel } from './icons.js';
import { installTooltips } from './tooltips.js';

document.addEventListener('contextmenu', event => event.preventDefault());
self.MonacoEnvironment = { getWorker: () => new Worker(new URL('editor.worker.js', location.href)) };
monaco.editor.defineTheme('gitx-merge', editorTheme);
const options = {theme:'gitx-merge',automaticLayout:true,minimap:{enabled:false},...editorTypography,padding:{top:4,bottom:14},
  scrollBeyondLastLine:false,wordWrap:'off',folding:false,contextmenu:false,links:false,stickyScroll:{enabled:false},lineNumbersMinChars:3,experimentalEditContextEnabled:false,
  unicodeHighlight:{ambiguousCharacters:false},renderValidationDecorations:'off',find:{addExtraSpaceOnTop:false}};
const panes=['left','result','right'].map((id,i)=>monaco.editor.create(document.getElementById(id),{...options,readOnly:i!==1,domReadOnly:i!==1,ariaLabel:i===1?'合并结果，可编辑':i===0?'当前版本，只读':'传入版本，只读'}));
const result=panes[1], marks=panes.map(p=>p.createDecorationsCollection());
document.fonts.load('14px GitXIdeaMono').then(()=>{monaco.editor.remeasureFonts();panes.forEach(p=>p.layout());});
installTooltips('.gutter-actions button');
let session='',doc,model,models=[],blocks=[],selected=-1,revision=0,saveState,busy=false,composing=false,restoring=false,transaction=false;
let rows=[],zoneIds=[[],[],[]],syncing=false,layoutTimer,currentState,undo=[],redo=[],active=1,disposed=false;
const send=value=>window.invokeCSharpAction?.(JSON.stringify({...value,session}));
installNativeBridge(window, () => {
  const owner = session, version = revision;
  return error => { if (!disposed && owner === session && version === revision) send({ type: 'error', message: String(error) }); };
});
const currentStates=()=>JSON.stringify(blocks.map(b=>b.choice));
const dirty=()=>!!model && saveState.dirty(model.getValue(),currentStates());
const range=block=>model.getDecorationRange(block.track);
const automaticChoice=block=>canApplyAutomatically(block,block.choice,model.getValueInRange(range(block)))?decide(block,block.choice,'automatic'):null;
function capture(){return {text:model.getValue(),blocks:blocks.map(b=>{const r=range(b);return {start:model.getOffsetAt(r.getStartPosition()),length:model.getOffsetAt(r.getEndPosition())-model.getOffsetAt(r.getStartPosition()),choice:{...b.choice}};})};}
function remember(){undo.push(currentState);redo=[];let size=0;for(let i=undo.length-1;i>=0;i--){size+=undo[i].text.length*2;if(size>16*1024*1024||undo.length-i>64){undo.splice(0,i+1);break;}}}
function installBlocks(states){
  const old=blocks.map(b=>b.track); blocks=doc.blocks.map((b,i)=>({...b,choice:states?.[i]?.choice??initialChoice(b)}));
  blocks.forEach(b=>b.resolved=resolvedChoice(b.choice));
  const ids=model.deltaDecorations(old,blocks.map((b,i)=>{const start=states?.[i]?.start??b.startOffset,length=states?.[i]?.length??b.length;
    const a=model.getPositionAt(start),z=model.getPositionAt(start+length);return {range:new monaco.Range(a.lineNumber,a.column,z.lineNumber,z.column),options:{stickiness:monaco.editor.TrackedRangeStickiness.AlwaysGrowsWhenTypingAtEdges}};}));
  blocks.forEach((b,i)=>b.track=ids[i]);
}
function status(){
  const remaining=blocks.filter(b=>!b.resolved).length;
  send({type:'state',revision,dirty:dirty(),edited:saveState.edited(model.getValue(),currentStates()),remaining,conflicts:blocks.filter(b=>b.isConflict&&!b.resolved).length,canAutomatic:blocks.some(b=>automaticChoice(b)!==null),selected,canUndo:undo.length>0,tabMovesFocus:result.getOption(monaco.editor.EditorOption.tabFocusMode)});
}
function finish(){revision++;currentState=capture();status();paint();clearTimeout(layoutTimer);layoutTimer=setTimeout(()=>{if(!disposed)send({type:'layout',revision,text:model.getValue()});},250);}
function restore(state){restoring=true;model.setValue(state.text);installBlocks(state.blocks);restoring=false;finish();}
function navigate(direction){
  const unresolved=blocks.map((b,i)=>!b.resolved?i:-1).filter(i=>i>=0);
  if(!unresolved.length){selected=-1;status();return;}
  selected=direction>0?(unresolved.find(i=>i>selected)??unresolved[0]):(unresolved.findLast(i=>selected<0||i<selected)??unresolved.at(-1));
  const r=range(blocks[selected]); result.setPosition(r.getStartPosition());result.revealLineInCenter(r.startLineNumber);result.focus();status();paint();
}
function editBlock(block,decision){
  if(decision.text!==undefined){
    const text=decision.text;
    const start=model.getOffsetAt(range(block).getStartPosition());
    result.executeEdits('gitx-merge',[{range:range(block),text,forceMoveMarkers:true}]);
    // Replacement collapses tracked ranges at its end; explicitly track the new
    // candidate so repeated choices and subsequent edits still target this block.
    const a=model.getPositionAt(start),z=model.getPositionAt(start+text.length);
    [block.track]=model.deltaDecorations([block.track],[{range:new monaco.Range(a.lineNumber,a.column,z.lineNumber,z.column),options:{stickiness:monaco.editor.TrackedRangeStickiness.AlwaysGrowsWhenTypingAtEdges}}]);
  }
  block.choice=decision.choice;block.resolved=resolvedChoice(block.choice);
}
function applyChoice(command){
  if(selected<0)return;
  const block=blocks[selected],decision=decide(block,block.choice,command);
  if(!decision)return;
  remember();transaction=true;editBlock(block,decision);transaction=false;finish();result.focus();
}
async function command(command){
  if(!model||busy||composing)return;
  if(command==='next'||command==='previous')navigate(command==='next'?1:-1);
  else if(command==='find'){panes[active].focus();panes[active].trigger('keyboard','actions.find',null);}
  // Keep the standard Ctrl+M behavior on the public per-editor option, which
  // also provides the state for the native hint (unlike global TabFocus).
  else if(command==='toggle-tab'){result.updateOptions({tabFocusMode:!result.getOption(monaco.editor.EditorOption.tabFocusMode)});panes[active].focus();}
  else if(['left','right','ignore-left','ignore-right','resolve-left','resolve-right','both','keep'].includes(command))applyChoice(command);
  else if(command==='undo'&&undo.length){redo.push(currentState);restore(undo.pop());}
  else if(command==='redo'&&redo.length){undo.push(currentState);restore(redo.pop());}
  else if(command==='automatic'){
    const pending=blocks.map(block=>({block,decision:automaticChoice(block)})).filter(x=>x.decision);
    if(!pending.length)return;
    remember();transaction=true;for(const {block,decision} of pending.reverse())editBlock(block,decision);transaction=false;finish();
  }
  else if(command==='save'){if(blocks.some(b=>!b.resolved))send({type:'error',message:'请先逐处确认冲突，再保存结果。'});else send({type:'save',revision,text:model.getValue()});}
  else if(command==='resolve'&&!dirty()&&!blocks.some(b=>!b.resolved)){send({type:'resolve',revision});}
  else if(command==='apply')send({type:'apply',revision,text:model.getValue()});
  else if(command.startsWith('compare-'))send({type:'compare',pair:command.slice(8),revision,text:model.getValue()});
}
function applyLayout(layout){
  const top=result.getScrollTop(),left=result.getScrollLeft();syncing=true;
  rows=threeWayRows(layout.left,layout.right);
  const zones=alignmentZones(rows,panes.map(p=>p.getModel().getLineCount()));
  panes.forEach((pane,side)=>pane.changeViewZones(accessor=>{
    zoneIds[side].forEach(id=>accessor.removeZone(id));zoneIds[side]=zones[side].map(zone=>{const node=document.createElement('div');node.className='gap';return accessor.addZone({afterLineNumber:zone.afterLineNumber,heightInLines:zone.height,domNode:node});});
  }));
  panes.forEach(p=>p.setScrollPosition({scrollTop:top,scrollLeft:left}));syncing=false;
  paint(layout);
}
let lastLayout;
function paint(layout=lastLayout){
  if(!model)return;if(layout)lastLayout=layout;
  const decorations=[[],[],[]],conflictLines=[new Set(),new Set(),new Set()];
  for(const block of blocks){const r=range(block);if(!r)continue;
    const kind=block.resolved?'handled':block.isConflict?'conflict':'changed';
    decorations[1].push({range:new monaco.Range(r.startLineNumber,1,lastLine(r),1),options:{isWholeLine:true,className:r.isEmpty()?`empty-${kind}`:kind,linesDecorationsClassName:block.id===selected?'current-block':undefined}});
    for(const side of [0,2])for(const line of blockSourceLines(block,side)){
      conflictLines[side].add(line);
      decorations[side].push({range:new monaco.Range(line,1,line,1),options:{isWholeLine:true,className:kind,zIndex:1}});
    }
  }
  if(layout)for(const [side,diff] of [[0,layout.left],[2,layout.right]])for(const block of diff.blocks)for(const row of block.rows)if(row.oldLine!==null&&!conflictLines[side].has(row.oldLine))
    decorations[side].push({range:new monaco.Range(row.oldLine,1,row.oldLine,1),options:{isWholeLine:true,className:'changed'}});
  marks.forEach((mark,side)=>mark.set(decorations[side]));connectors();
}
function lastLine(r){return Math.max(r.startLineNumber,r.endLineNumber-(r.endColumn===1?1:0));}
const gutterActions=new Map();
function blockSourceLines(block,side){const start=side===0?block.leftStart:block.rightStart,count=side===0?block.leftCount:block.rightCount;return Array.from({length:count},(_,i)=>start+i+1);}
function actionsFor(side,block){
  const key=`${side}:${block.id}`;
  if(!gutterActions.has(key)){
    const group=document.createElement('div');group.className='gutter-actions';
    for(const [action,icon,label] of [[side===0?'left':'right',side===0?'right':'left',side===0?'接受左侧':'接受右侧'],[side===0?'ignore-left':'ignore-right','close',side===0?'忽略左侧':'忽略右侧']]){
      const button=document.createElement('button');button.type='button';button.dataset.tooltip=`冲突 ${block.id+1} · ${label}`;button.setAttribute('aria-label',button.dataset.tooltip);iconLabel(button,icon);
      button.onclick=()=>{if(busy||composing)return;selected=blocks.findIndex(b=>b.id===block.id);command(action);};group.append(button);
    }
    document.getElementById(side===0?'left-connectors':'right-connectors').parentElement.append(group);gutterActions.set(key,group);
  }
  return gutterActions.get(key);
}
function connectors(){
  if(!model)return;
  for(const [side,id] of [[0,'left-connectors'],[2,'right-connectors']]){
    const svg=document.getElementById(id);svg.replaceChildren();
    for(const block of blocks){const r=range(block);if(!r){const group=gutterActions.get(`${side}:${block.id}`);if(group)group.hidden=true;continue;}
      const top=result.getTopForLineNumber(r.startLineNumber)-result.getScrollTop(),bottom=result.getTopForLineNumber(r.endLineNumber)+(r.endColumn>1?options.lineHeight:0)-result.getScrollTop();
      const group=actionsFor(side,block);group.hidden=top>svg.clientHeight||bottom<0||block.choice[side===0?'left':'right']!=='pending';
      group.classList.toggle('done',block.resolved);group.style.top=`${Math.max(0,Math.min(svg.clientHeight-20,(top+bottom)/2-10))}px`;
      group.querySelectorAll('button').forEach(button=>button.disabled=busy);
      if(top>svg.clientHeight&&bottom>svg.clientHeight||top<0&&bottom<0)continue;
      const source=blockSourceLines(block,side),boundary=(side===0?block.leftStart:block.rightStart)+1;
      const sourceTop=panes[side].getTopForLineNumber(source[0]??boundary)-panes[side].getScrollTop();
      const sourceBottom=source.length?panes[side].getTopForLineNumber(source.at(-1))+options.lineHeight-panes[side].getScrollTop():sourceTop+1;
      const p=document.createElementNS('http://www.w3.org/2000/svg','path');
      const width=svg.clientWidth;
      const a=side===0?sourceTop:top,b=side===0?sourceBottom:Math.max(top+1,bottom),c=side===0?top:sourceTop,d=side===0?Math.max(top+1,bottom):sourceBottom;
      p.setAttribute('d',`M0,${a} C${width*.5},${a} ${width*.5},${c} ${width},${c} L${width},${d} C${width*.5},${d} ${width*.5},${b} 0,${b} Z`);
      p.setAttribute('class','connection'+(block.resolved?' done':block.isConflict?'':' changed'));svg.append(p);
    }
  }
}
panes.forEach((pane,side)=>{
  pane.onDidFocusEditorWidget(()=>active=side);
  pane.onDidScrollChange(e=>{if(!syncing){syncing=true;panes.forEach((other,i)=>{if(i!==side)other.setScrollPosition({scrollTop:e.scrollTop,scrollLeft:e.scrollLeft});});syncing=false;}connectors();});
  pane.onDidLayoutChange(connectors);
});
result.onDidCompositionStart(()=>composing=true);result.onDidCompositionEnd(()=>composing=false);
result.onDidChangeConfiguration(event=>{if(model&&event.hasChanged(monaco.editor.EditorOption.tabFocusMode))status();});
result.onDidChangeModelContent(()=>{if(!restoring&&!transaction&&currentState){remember();finish();}});
result.onDidChangeCursorPosition(e=>{if(!model||restoring||transaction)return;const index=blocks.findIndex(b=>{const r=range(b);return r&&r.containsPosition(e.position);});if(index>=0){selected=index;status();}});
window.addEventListener('keydown',event=>{
  if(event.isComposing||composing)return;
  let action=event.key==='F7'?(event.shiftKey?'previous':'next'):event.ctrlKey&&event.key.toLowerCase()==='s'?'save':event.ctrlKey&&event.key.toLowerCase()==='z'?(event.shiftKey?'redo':'undo'):event.ctrlKey&&event.key.toLowerCase()==='y'?'redo':null;
  if(action){event.preventDefault();event.stopImmediatePropagation();command(action);}
  else if(event.ctrlKey&&event.key.toLowerCase()==='f'){event.preventDefault();event.stopImmediatePropagation();panes[active].focus();panes[active].trigger('keyboard','actions.find',null);}
  else if(event.ctrlKey&&!event.altKey&&!event.shiftKey&&event.key.toLowerCase()==='m'){event.preventDefault();event.stopImmediatePropagation();command('toggle-tab');}
},true);
installFocusBoundary(direction=>send({type:'focus-out',direction}));
window.gitxMerge={command,busy(value){busy=value;result.updateOptions({readOnly:busy,domReadOnly:busy});status();connectors();},saved(version){if(version===revision){saveState.markSaved(model.getValue(),currentStates());status();}},
  async layout(version){const response=await fetch('layout',{cache:'no-store'});const payload=await response.json();if(!disposed&&payload.session===session&&payload.revision===revision&&version===revision)applyLayout(payload.layout);}};
window.addEventListener('beforeunload',()=>{disposed=true;clearTimeout(layoutTimer);panes.forEach(p=>p.dispose());models.forEach(m=>m.dispose());undo=[];redo=[];});
(async()=>{try{
  const response=await fetch('document',{cache:'no-store'});const payload=await response.json();session=payload.session;doc=payload.document;
  const language=languageForPath(doc.path);
  await prepareLanguage(language);
  if(disposed)return;
  models=[doc.left.text,doc.result,doc.right.text].map((text,i)=>monaco.editor.createModel(i===1?text:text.replace(/\n$/,''),language));model=models[1];
  panes.forEach((pane,i)=>{pane.setModel(models[i]);if(i!==1)pane.updateOptions({lineNumbers:n=>n<=lineCount(i===0?doc.left.text:doc.right.text)?String(n):''});});
  for(const [id,side] of [['left',doc.left],['right',doc.right]]){
    document.getElementById(`${id}-label`).textContent=side.label;
    document.getElementById(`${id}-version`).textContent=side.sha?`blob ${side.sha.slice(0,8)}`:'删除';
    document.getElementById(`${id}-header`).title=`${side.label} · ${side.path} · ${side.sha??'删除'}`;
  }
  document.getElementById('result-header').title=doc.path;
  installBlocks();saveState=new MergeSaveState(doc.result,currentStates(),doc.existingResult);currentState=capture();applyLayout(doc.layout);
  document.getElementById('load-status').hidden=true;
  send({type:'ready'});status();if(blocks.length)navigate(1);
}catch(error){send({type:'error',message:String(error)});const status=document.getElementById('load-status');status.hidden=false;status.textContent=String(error);}})();
