(()=>{
  const root=document.getElementById('kb-fixed-drop');
  const phone=root?.querySelector('.phone');
  if(!phone)return;

  const menu=document.createElement('div');
  menu.className='row-context-menu';
  menu.setAttribute('role','menu');
  menu.hidden=true;
  phone.append(menu);
  let owner=null,trigger=null,onClose=null;

  const confirmLayer=document.createElement('div');
  confirmLayer.className='app-confirm-layer';
  confirmLayer.hidden=true;
  confirmLayer.innerHTML='<div class="app-confirm-card" role="dialog" aria-modal="true" aria-labelledby="appConfirmTitle"><div class="app-confirm-title" id="appConfirmTitle"></div><div class="app-confirm-body"></div><div class="app-confirm-actions"><button class="app-confirm-cancel" type="button">Отмена</button><button class="app-confirm-accept" type="button">Подтвердить</button></div></div>';
  phone.append(confirmLayer);
  const confirmTitle=confirmLayer.querySelector('.app-confirm-title');
  const confirmBody=confirmLayer.querySelector('.app-confirm-body');
  const confirmCancel=confirmLayer.querySelector('.app-confirm-cancel');
  const confirmAccept=confirmLayer.querySelector('.app-confirm-accept');
  let confirmCallback=null,previousFocus=null;

  function closeConfirm(){
    if(confirmLayer.hidden)return;
    confirmLayer.hidden=true;
    confirmCallback=null;
    if(previousFocus?.isConnected)previousFocus.focus({preventScroll:true});
    previousFocus=null;
  }
  function confirmAction({title,body='',confirmLabel='Подтвердить',onConfirm}){
    close();
    closeConfirm();
    previousFocus=document.activeElement;
    confirmCallback=onConfirm;
    confirmTitle.textContent=title;
    confirmBody.textContent=body;
    confirmBody.hidden=!body;
    confirmAccept.textContent=confirmLabel;
    confirmLayer.hidden=false;
    confirmCancel.focus({preventScroll:true});
  }
  confirmCancel.addEventListener('click',closeConfirm);
  confirmAccept.addEventListener('click',()=>{const callback=confirmCallback;closeConfirm();callback?.();});
  confirmLayer.addEventListener('pointerdown',event=>{if(event.target===confirmLayer)closeConfirm();});
  confirmLayer.addEventListener('keydown',event=>{
    if(event.key==='Escape'){event.preventDefault();event.stopPropagation();closeConfirm();}
    if(event.key==='Tab'){
      if(event.shiftKey&&document.activeElement===confirmCancel){event.preventDefault();confirmAccept.focus();}
      else if(!event.shiftKey&&document.activeElement===confirmAccept){event.preventDefault();confirmCancel.focus();}
    }
  });

  function close(){
    owner?.classList.remove('context-active');
    trigger?.setAttribute('aria-expanded','false');
    const callback=onClose;
    owner=null;trigger=null;onClose=null;menu.hidden=true;menu.replaceChildren();
    callback?.();
  }

  function isOpen(){return !menu.hidden;}
  function isOpenFor(candidate){return !menu.hidden&&owner===candidate;}

  function position(anchor,point){
    const phoneRect=phone.getBoundingClientRect();
    const anchorRect=anchor.getBoundingClientRect();
    const navTop=root.querySelector('#bottomNav')?.getBoundingClientRect().top??window.innerHeight;
    const leftLimit=Math.max(phoneRect.left+8,8);
    const rightLimit=Math.min(phoneRect.right-8,window.innerWidth-8);
    const headerBottom=root.querySelector('.topline')?.getBoundingClientRect().bottom??phoneRect.top;
    const topLimit=Math.max(phoneRect.top+8,headerBottom+8,8);
    const bottomLimit=Math.min(navTop-8,window.innerHeight-8);
    const width=menu.offsetWidth,height=menu.offsetHeight;
    let x,y;
    if(point){
      x=point.x+8;
      y=point.y+8;
      if(x+width>rightLimit)x=point.x-width-8;
      if(y+height>bottomLimit)y=point.y-height-8;
    }else{
      x=anchorRect.right-width;
      y=anchorRect.top-height-6;
      if(y<topLimit)y=anchorRect.bottom+6;
      if(y+height>bottomLimit)y=anchorRect.top-height-6;
    }
    x=Math.max(leftLimit,Math.min(x,rightLimit-width));
    y=Math.max(topLimit,Math.min(y,bottomLimit-height));
    menu.style.left=`${x-phoneRect.left}px`;
    menu.style.top=`${y-phoneRect.top}px`;
    menu.style.setProperty('--menu-enter-y',y<anchorRect.top?'6px':'-6px');
  }

  function open({owner:nextOwner,anchor,items,point,trigger:nextTrigger,onClose:nextOnClose,label='Действия'}){
    if(!nextOwner||!anchor||!items?.length)return;
    close();
    owner=nextOwner;trigger=nextTrigger||null;onClose=nextOnClose||null;
    owner.classList.add('context-active');
    trigger?.setAttribute('aria-expanded','true');
    menu.setAttribute('aria-label',label);
    for(const item of items){
      const button=document.createElement('button');
      button.type='button';
      button.className=`row-context-item${item.danger?' danger':''}`;
      button.setAttribute('role','menuitem');
      button.textContent=item.label;
      button.addEventListener('click',event=>{event.stopPropagation();close();item.action();});
      menu.append(button);
    }
    menu.classList.remove('opening');
    menu.hidden=false;
    position(anchor,point);
    void menu.offsetWidth;
    menu.classList.add('opening');
  }

  menu.addEventListener('keydown',event=>{
    const buttons=[...menu.querySelectorAll('button')];
    const index=buttons.indexOf(document.activeElement);
    if(event.key==='ArrowDown'||event.key==='ArrowUp'){
      event.preventDefault();
      buttons[(index+(event.key==='ArrowDown'?1:-1)+buttons.length)%buttons.length]?.focus();
    }else if(event.key==='Home'||event.key==='End'){
      event.preventDefault();buttons[event.key==='Home'?0:buttons.length-1]?.focus();
    }
  });
  document.addEventListener('pointerdown',event=>{
    if(!menu.hidden&&!menu.contains(event.target)&&!event.target.closest('.row-menu-trigger,.planning-menu-trigger'))close();
  },true);
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&!menu.hidden){event.preventDefault();close();}});
  document.addEventListener('scroll',event=>{if(!menu.hidden&&!menu.contains(event.target))close();},true);
  window.addEventListener('resize',close);
  window.personalOSContextMenu={open,close,isOpen,isOpenFor,confirmAction};
})();
