import './tooltips.css';
let warmUntil = 0;

// Draw above the WebView surface, outside clipped gutters. Keep the accessible
// name on the control itself; this transient tooltip never joins the tab order.
export function installTooltips(selector) {
  const tip=document.createElement('div');
  tip.className='gitx-tooltip';tip.setAttribute('role','tooltip');tip.hidden=true;
  document.body.append(tip);
  let target=null,timer;
  const hide=()=>{clearTimeout(timer);if(!tip.hidden)warmUntil=performance.now()+120;tip.hidden=true;target=null;};
  const show=element=>{
    if(!element||element.disabled||element===target)return;
    hide();target=element;
    const text=element.getAttribute('title')||element.dataset.tooltip;
    if(!text)return;
    element.dataset.tooltip=text;element.removeAttribute('title');
    const instant=performance.now()<warmUntil;tip.classList.toggle('instant',instant);
    timer=setTimeout(()=>{
      if(!element.isConnected)return;
      if(target!==element)return;
      tip.textContent=text;tip.hidden=false;
      const rect=element.getBoundingClientRect();
      tip.style.left=`${Math.max(4,Math.min(innerWidth-tip.offsetWidth-4,rect.left))}px`;
      const above=rect.top-tip.offsetHeight-8;
      tip.style.top=`${Math.max(4,Math.min(innerHeight-tip.offsetHeight-4,above>=4?above:rect.bottom+8))}px`;
    },instant?0:400);
  };
  for(const type of ['pointerover','focusin'])document.addEventListener(type,event=>show(event.target.closest?.(selector)));
  for(const type of ['pointerout','focusout'])document.addEventListener(type,event=>{
    if(event.target.closest?.(selector)===target&&!target?.contains(event.relatedTarget))hide();
  });
  document.addEventListener('pointerdown',hide);
  document.addEventListener('keydown',event=>{if(event.key==='Escape')hide();});
  document.addEventListener('scroll',hide,true);
  window.addEventListener('blur',hide);
}
