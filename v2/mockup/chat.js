(()=>{
  const root=document.getElementById('kb-fixed-drop');
  if(!root)return;

  const $=selector=>root.querySelector(selector);
  const chat=$('#chatFX'),openButton=$('#agentChatFX'),backButton=$('#chatBackFX'),historyButton=$('#chatHistoryFX');
  const historyOverlay=$('#chatHistoryOverlayFX'),historyClose=$('#chatHistoryCloseFX');
  const title=$('#titleFX'),bottomWindow=$('.bottom-window'),scroll=$('#chatScrollFX');
  const list=$('#chatListFX'),form=$('#chatFormFX'),input=$('#chatInputFX');
  const screens=['#knowledgeFX','#docFX','#planningFX','#tasksFX','#placeholderFX'].map($).filter(Boolean);
  const state={open:false,previousTitle:'',previousVisibility:[],conversationEpoch:0};
  function resizeInput(){
    input.style.height='auto';
    input.style.height=Math.min(input.scrollHeight,112)+'px';
    input.classList.toggle('is-overflowing',input.scrollHeight>112);
  }

  function scrollToLatest(){scroll.scrollTop=scroll.scrollHeight}
  function closeHistory(restoreFocus=true){
    const wasOpen=historyOverlay.classList.contains('open');
    historyOverlay.classList.remove('open');
    historyOverlay.setAttribute('aria-hidden','true');
    historyButton.setAttribute('aria-expanded','false');
    if(wasOpen&&restoreFocus&&state.open)historyButton.focus({preventScroll:true});
  }
  function openHistory(){
    if(!state.open)return;
    historyOverlay.classList.add('open');
    historyOverlay.setAttribute('aria-hidden','false');
    historyButton.setAttribute('aria-expanded','true');
    historyClose.focus({preventScroll:true});
  }
  function openChat(){
    if(state.open)return;
    const otherOverlay=$('#overlayFX');
    if(otherOverlay?.classList.contains('open'))otherOverlay.click();
    state.previousTitle=title.textContent;
    state.previousVisibility=screens.map(screen=>!screen.hidden);
    screens.forEach(screen=>screen.hidden=true);
    chat.hidden=false;
    bottomWindow.hidden=true;
    title.textContent='Агент';
    openButton.hidden=true;
    backButton.hidden=false;
    historyButton.hidden=false;
    state.open=true;
    requestAnimationFrame(scrollToLatest);
  }
  function closeChat(restoreScreen=true){
    if(!state.open)return;
    closeHistory(false);
    chat.hidden=true;
    if(restoreScreen){
      screens.forEach((screen,index)=>screen.hidden=!state.previousVisibility[index]);
      title.textContent=state.previousTitle;
    }
    bottomWindow.hidden=false;
    openButton.hidden=false;
    backButton.hidden=true;
    historyButton.hidden=true;
    state.open=false;
    state.previousVisibility=[];
    backButton.blur();
  }
  function addMessage(role,message){
    const row=document.createElement('div');
    const bubble=document.createElement('div');
    row.className='chat-row '+role;
    bubble.className='chat-bubble';
    bubble.textContent=message;
    row.append(bubble);
    list.append(row);
    scrollToLatest();
  }
  function startConversation(message){
    state.conversationEpoch++;
    list.replaceChildren();
    addMessage('agent',message);
    closeHistory(false);
  }

  openButton.addEventListener('click',openChat);
  backButton.addEventListener('click',()=>closeChat());
  historyButton.addEventListener('click',()=>historyOverlay.classList.contains('open')?closeHistory():openHistory());
  historyClose.addEventListener('click',()=>closeHistory());
  historyOverlay.addEventListener('click',event=>{if(event.target===historyOverlay)closeHistory()});
  $('#chatNewFX').addEventListener('click',()=>{
    startConversation('Новый чат. Что нужно сделать?');
    input.focus({preventScroll:true});
  });
  root.querySelectorAll('[data-chat-history]').forEach(button=>button.addEventListener('click',()=>{
    startConversation('Открыт чат: «'+button.dataset.chatHistory+'».');
  }));
  form.addEventListener('submit',event=>{
    event.preventDefault();
    const message=input.value.trim();
    if(!message)return;
    addMessage('user',message);
    input.value='';
    resizeInput();
    const epoch=state.conversationEpoch;
    setTimeout(()=>{
      if(epoch===state.conversationEpoch)addMessage('agent','Понял. В рабочей версии отвечу с учётом данных Personal OS.');
    },260);
  });
  input.addEventListener('keydown',event=>{
    if(event.key==='Enter'&&!event.shiftKey){event.preventDefault();form.requestSubmit()}
  });
  input.addEventListener('input',resizeInput);
  document.addEventListener('keydown',event=>{
    if(event.key!=='Escape'||!state.open)return;
    if(historyOverlay.classList.contains('open'))closeHistory();
    else closeChat();
  });
  document.addEventListener('personalos:pagechange',()=>{
    if(state.open)closeChat(false);
  });
})();
