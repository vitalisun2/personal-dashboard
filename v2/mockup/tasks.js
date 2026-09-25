(()=>{
  const model=window.personalOS;
  const view=document.getElementById('tasksFX');
  if(!model||!view)return;

  const root=document.getElementById('kb-fixed-drop');
  const contextMenu=window.personalOSContextMenu;
  const $=selector=>root.querySelector(selector);
  const board=$('#taskBoardFX'),groups=$('#taskGroupsFX'),empty=$('#taskEmptyFX'),filters=$('#taskFiltersFX');
  const archiveTop=$('#taskArchiveTopFX'),archiveBar=$('#taskArchiveBarFX'),archiveOpen=$('#taskArchiveOpenFX'),archiveBack=$('#taskArchiveBackFX');
  const toggleAll=$('#toggleTaskSectionsFX'),orderToggle=$('#taskOrderFX'),add=$('#addTaskFX'),detail=$('#taskDetailFX'),overlay=$('#overlayFX'),sheet=$('#taskCreateFX');
  const collapseIcon=inward=>`<svg viewBox="0 0 20 20" aria-hidden="true"><path d="${inward?'M5 3 10 8 15 3':'M5 8 10 3 15 8'}"/><path d="${inward?'M5 17 10 12 15 17':'M5 12 10 17 15 12'}"/></svg>`;
  const sectionList=$('#taskSectionListFX'),toast=$('#taskToastFX'),title=$('#taskTitleFX'),titleInput=$('#taskTitleInputFX');
  const description=$('#taskDescriptionFX'),descriptionInput=$('#taskDescriptionInputFX'),sectionLabel=$('#taskSectionFX');
  const moveButton=$('#taskMoveFX'),statusButton=$('#taskStatusFX'),deleteButton=$('#taskDeleteFX');
  const state={tab:'backlog',filter:'all',archive:false,selectedId:null,revealed:null,expanded:{backlog:new Set(),today:new Set()},sections:{backlog:[],today:[]},createType:'task',createSectionId:'',titleEditing:false,descriptionEditing:false,orderMode:false};
  let swipe=null,drag=null,suppressUntil=0,toastTimer=0;
  const linked=task=>Boolean(task?.projectId&&task?.featureId);
  const getTask=id=>model.getTask(id),getProject=id=>model.getProject(id);
  const dots=()=>'<span>'+Array.from({length:6},()=>'<i></i>').join('')+'</span>';
  const pencil=()=>'<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20l4.2-1 10.3-10.3a2.1 2.1 0 0 0-3-3L5.2 16 4 20Z"></path><path d="m13.8 7.2 3 3"></path></svg>';
  const trash=()=>'<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"></path><path d="M9 7V4h6v3"></path><path d="m6 7 1 13h10l1-13"></path><path d="M10 11v5M14 11v5"></path></svg>';
  const workState=task=>task.completed?'completed':task.workStatus==='active'?'in_progress':'new';
  const workLabel=value=>({new:'Новая',in_progress:'В работе',completed:'Готово'})[value]||'Новая';
  const uniqueId=prefix=>`${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2,6)}`;
  deleteButton.innerHTML=trash();

  function flash(message){clearTimeout(toastTimer);toast.textContent=message;toast.classList.add('show');toastTimer=setTimeout(()=>toast.classList.remove('show'),1600);}
  function projectTitle(projectId){return getProject(projectId)?.title||'Проект';}
  function originPath(task){
    if(!linked(task))return task.section||'Личное';
    const project=getProject(task.projectId),milestone=project?.milestones.find(item=>item.id===task.milestoneId),feature=milestone?.features.find(item=>item.id===task.featureId);
    return [project?.title||'Проект',milestone?.title,feature?.title].filter(Boolean).join(' › ');
  }
  function sectionKey(task){return linked(task)?`project:${task.projectId}`:`plain:${task.section||'Личное'}`;}
  function sectionTitle(section){return section.projectId?projectTitle(section.projectId):section.title;}
  function sectionForKey(bucket,key){return state.sections[bucket].find(section=>(section.projectId?`project:${section.projectId}`:`plain:${section.title}`)===key);}
  function taskInSection(task,section){return section.projectId?task.projectId===section.projectId||(!linked(task)&&(task.section||'Личное')===projectTitle(section.projectId)):!linked(task)&&(task.section||'Личное')===section.title;}
  function addSection(bucket,section){
    const existing=sectionForKey(bucket,section.projectId?`project:${section.projectId}`:`plain:${section.title}`);if(existing)return existing;
    const created={id:section.id||uniqueId('section'),title:section.title||'',projectId:section.projectId||null};state.sections[bucket].push(created);state.expanded[bucket].add(created.id);return created;
  }
  function ensureSectionForTask(bucket,task){
    if(linked(task))return addSection(bucket,{id:`project-${bucket}-${task.projectId}`,projectId:task.projectId});
    const title=task.section||'Личное';
    return state.sections[bucket].find(section=>section.projectId&&sectionTitle(section)===title)||addSection(bucket,{title});
  }
  function syncSections(){for(const bucket of ['backlog','today'])for(const task of model.tasks.filter(item=>item.status===bucket))ensureSectionForTask(bucket,task);}

  function closeReveal(){contextMenu.close();state.revealed=null;}
  function menuItems(kind,id,label){
    if(kind==='task'){
      const task=getTask(id);if(!task)return [];
      const items=[];
      if(task.status==='today'){
        const next=workState(task)==='new'?'Отметить «В работе»':workState(task)==='in_progress'?'Отметить «Готово»':'Завершить и в архив';
        items.push({label:next,action:()=>advanceStatus(id)});
      }
      items.push({label:task.status==='backlog'?'Перенести в Сегодня':'Вернуть в Backlog',action:()=>moveTask(id,task.status==='backlog'?'today':'backlog')});
      if(task.status==='backlog'&&linked(task))items.push({label:'Вернуть в план',action:()=>model.moveTaskToPlan(id)});
      items.push({label:'Переименовать',action:()=>renameTaskInline(id)});
      if(task.status!=='archived')items.push({label:'Убрать в архив',danger:true,action:()=>archiveTask(id)});
      return items;
    }
    return [
      {label:'Переименовать',action:()=>renameSectionInline(id)},
      {label:'Удалить раздел',danger:true,action:()=>deleteSection(id)}
    ];
  }
  function showMenu(kind,id,wrap,row,point=null,trigger=null){
    if(state.orderMode)return;
    const label=kind==='task'?getTask(id)?.title:sectionTitle(state.sections[state.tab].find(item=>item.id===id));
    const items=menuItems(kind,id,label);if(!items.length)return;
    contextMenu.open({owner:wrap,anchor:row,items,point,trigger,
      label:'Действия с '+(kind==='task'?'задачей':'разделом')+' «'+label+'»',
      onClose:()=>{state.revealed=null;}});
    state.revealed=kind+':'+id;
  }
  function menuTrigger(kind,id,label,wrap,row){
    const button=document.createElement('button');button.type='button';button.className='row-menu-trigger';button.textContent='⋯';
    button.setAttribute('aria-label','Действия с '+label);button.setAttribute('aria-haspopup','menu');button.setAttribute('aria-expanded','false');button.title='Действия';button.hidden=state.orderMode;
    button.addEventListener('click',event=>{event.stopPropagation();contextMenu.isOpenFor(wrap)?closeReveal():showMenu(kind,id,wrap,row,null,button);});
    return button;
  }
  function bindSwipe(wrap,row,key){
    const kind=key.startsWith('task:')?'task':'section',id=key.slice(kind.length+1);
    const cancelTimer=()=>{if(swipe?.timer){clearTimeout(swipe.timer);swipe.timer=null;}};
    row.addEventListener('pointerdown',event=>{
      if(state.orderMode||event.button>0||event.target.closest('.handle,.row-menu-trigger,.task-inline-input'))return;
      cancelTimer();
      const current={key,pointerId:event.pointerId,startX:event.clientX,startY:event.clientY,moved:false,long:false,timer:null};
      swipe=current;
      if(event.pointerType==='touch'||event.pointerType==='pen')current.timer=setTimeout(()=>{
        if(swipe!==current||current.moved)return;
        current.long=true;suppressUntil=Date.now()+350;showMenu(kind,id,wrap,row);
      },480);
      event.target.setPointerCapture?.(event.pointerId);
    });
    row.addEventListener('pointermove',event=>{
      if(!swipe||swipe.key!==key||swipe.pointerId!==event.pointerId)return;
      const dx=event.clientX-swipe.startX,dy=event.clientY-swipe.startY;
      if(Math.hypot(dx,dy)>8){swipe.moved=true;cancelTimer();}
      if(Math.abs(dy)>Math.abs(dx)&&Math.abs(dy)>10){swipe=null;return;}
      if(dx<-12&&Math.abs(dx)>Math.abs(dy)*1.1)event.preventDefault();
    },{passive:false});
    row.addEventListener('pointerup',event=>{
      if(!swipe||swipe.key!==key||swipe.pointerId!==event.pointerId)return;
      const current=swipe;cancelTimer();swipe=null;
      if(current.long)return;
      const dx=event.clientX-current.startX,dy=event.clientY-current.startY;
      if(Math.abs(dx)<Math.abs(dy)*1.2)return;
      if(dx<=-56){suppressUntil=Date.now()+320;showMenu(kind,id,wrap,row);}
      else if(dx>=45&&contextMenu.isOpenFor(wrap)){suppressUntil=Date.now()+320;closeReveal();}
    });
    row.addEventListener('pointercancel',event=>{if(swipe?.key===key&&swipe.pointerId===event.pointerId){cancelTimer();swipe=null;}});
    row.addEventListener('contextmenu',event=>{
      event.preventDefault();if(state.orderMode||contextMenu.isOpenFor(wrap))return;
      showMenu(kind,id,wrap,row,{x:event.clientX,y:event.clientY});
    });
  }
  view.addEventListener('click',event=>{if(Date.now()<suppressUntil&&!event.target.closest('.row-context-menu')){event.preventDefault();event.stopPropagation();}},true);

  function taskMatchesFilter(task){if(state.filter==='all')return true;return state.tab==='today'&&workState(task)===state.filter;}
  function renderTaskRow(task){
    const wrap=document.createElement('div'),revealKey=`task:${task.id}`;wrap.className='task-row-wrap';wrap.dataset.taskRevealKey=revealKey;
    const row=document.createElement('article');row.className='task-row';row.dataset.taskId=task.id;
    const open=document.createElement('button');open.type='button';open.className='task-open';open.addEventListener('click',()=>{if(state.orderMode)return;state.revealed?closeReveal():openTask(task.id);});
    if(task.status==='today'){const dot=document.createElement('span');dot.className=`task-status-dot ${workState(task)}`;open.append(dot);}
    const copy=document.createElement('span');copy.className='task-copy';const taskTitle=document.createElement('span');taskTitle.className='task-title';taskTitle.textContent=task.title;copy.append(taskTitle);
    open.append(copy);row.append(open,menuTrigger('task',task.id,task.title,wrap,row));
    if(task.status!=='archived'){const handle=document.createElement('button');handle.type='button';handle.className='handle';handle.dataset.taskDragKind='task';handle.dataset.taskDragId=task.id;handle.innerHTML=dots();handle.setAttribute('aria-label',`Перетащить ${task.title}`);handle.hidden=!state.orderMode;row.append(handle);}
    wrap.append(row);bindSwipe(wrap,row,revealKey);return wrap;
  }
  function render(){
    syncSections();const today=state.tab==='today';orderToggle.setAttribute('aria-pressed',String(state.orderMode));orderToggle.setAttribute('aria-label',state.orderMode?'Выключить сортировку':'Включить сортировку');orderToggle.title=state.orderMode?'Выключить сортировку':'Включить сортировку';root.querySelectorAll('[data-task-tab]').forEach(button=>button.classList.toggle('active',!state.archive&&button.dataset.taskTab===state.tab));root.querySelectorAll('[data-task-filter]').forEach(button=>button.classList.toggle('active',button.dataset.taskFilter===state.filter));root.querySelectorAll('.task-today-filter').forEach(button=>button.hidden=!today);
    filters.hidden=state.archive||!today;root.querySelector('.task-toolbar').hidden=state.archive;archiveTop.hidden=!state.archive;archiveBar.hidden=!(today&&!state.archive);toggleAll.hidden=state.archive;add.hidden=state.archive;groups.innerHTML='';groups.classList.toggle('archive-flat',state.archive);
    if(state.archive){const archived=model.tasks.filter(task=>task.status==='archived');archived.forEach(task=>groups.append(renderTaskRow(task)));empty.hidden=archived.length>0;empty.textContent='Архив пока пуст.';return;}
    for(const section of state.sections[state.tab]){
      const tasks=model.tasks.filter(task=>task.status===state.tab&&taskInSection(task,section)&&taskMatchesFilter(task));if((state.filter!=='all'||section.projectId)&&!tasks.length)continue;
      const group=document.createElement('section');group.className='task-group';group.dataset.taskGroup=section.id;
      const wrap=document.createElement('div'),revealKey=`section:${section.id}`;wrap.className='task-section-wrap';wrap.dataset.taskRevealKey=revealKey;
      const row=document.createElement('div');row.className='task-section-row';row.dataset.taskSectionId=section.id;
      const chevron=document.createElement('button');chevron.type='button';chevron.className=`chev ${state.expanded[state.tab].has(section.id)?'open':''}`;chevron.textContent='›';chevron.addEventListener('click',()=>toggleSection(section.id));
      const name=document.createElement('button');name.type='button';name.className='task-section-title';
      if(section.projectId){const diamond=document.createElement('span');diamond.className='task-project-diamond';diamond.setAttribute('aria-hidden','true');name.append(diamond);}
      const sectionName=document.createElement('span');sectionName.className='task-section-label';sectionName.textContent=sectionTitle(section);name.append(sectionName);
      name.addEventListener('click',()=>toggleSection(section.id));
      const handle=document.createElement('button');handle.type='button';handle.className='handle';handle.dataset.taskDragKind='section';handle.dataset.taskDragId=section.id;handle.innerHTML=dots();handle.setAttribute('aria-label',`Перетащить раздел ${sectionTitle(section)}`);
      handle.hidden=!state.orderMode;row.append(chevron,name,menuTrigger('section',section.id,sectionTitle(section),wrap,row),handle);wrap.append(row);group.append(wrap);bindSwipe(wrap,row,revealKey);
      if(state.expanded[state.tab].has(section.id)){const list=document.createElement('div');list.className='task-list';tasks.forEach(task=>list.append(renderTaskRow(task)));group.append(list);}groups.append(group);
    }
    empty.hidden=groups.children.length>0;empty.textContent='Здесь пока нет задач.';const ids=[...groups.querySelectorAll('[data-task-section-id]')].map(row=>row.dataset.taskSectionId),allOpen=ids.length>0&&ids.every(id=>state.expanded[state.tab].has(id)),label=allOpen?'Свернуть все разделы':'Развернуть все разделы';toggleAll.innerHTML=collapseIcon(allOpen);toggleAll.title=label;toggleAll.setAttribute('aria-label',label);
  }
  function toggleSection(id){if(state.orderMode)return;if(state.revealed){closeReveal();return;}const open=state.expanded[state.tab];open.has(id)?open.delete(id):open.add(id);render();}
  function notify(){model.notify();}
  function moveTask(id,target){const task=getTask(id);if(!task)return;task.status=target;if(target==='backlog'){task.workStatus='new';task.completed=false;}const section=ensureSectionForTask(target,task);state.expanded[target].add(section.id);state.revealed=null;notify();flash(target==='today'?'Добавлено в Сегодня':'Возвращено в Backlog');}
  function advanceStatus(id){const task=getTask(id);if(!task||task.status!=='today')return;const current=workState(task);if(current==='new'){task.workStatus='active';task.completed=false;flash('Статус: В работе');}else if(current==='in_progress'){task.workStatus='done';task.completed=true;flash('Статус: Готово');}else{task.status='archived';task.workStatus='done';task.completed=true;flash('Задача завершена и отправлена в архив');}state.revealed=null;notify();}
  function archiveTask(id){
    const task=getTask(id);if(!task)return;
    contextMenu.confirmAction({title:'Убрать задачу в архив?',body:`«${task.title}» можно будет найти в архиве.`,confirmLabel:'В архив',onConfirm:()=>{
      task.status='archived';state.revealed=null;notify();flash('Задача перемещена в архив');
    }});
  }
  function renameTaskInline(id){
    const task=getTask(id),wrap=groups.querySelector(`[data-task-reveal-key="task:${CSS.escape(id)}"]`);if(!task||!wrap)return;closeReveal();const row=wrap.querySelector('[data-task-id]'),open=row.querySelector('.task-open'),menuButton=row.querySelector('.row-menu-trigger');open.hidden=true;menuButton.hidden=true;
    const input=document.createElement('input');input.className='task-inline-input';input.value=task.title;row.insertBefore(input,menuButton);let done=false;
    const finish=save=>{if(done)return;done=true;const value=input.value.trim();if(save&&value){task.title=value;notify();}else render();};input.addEventListener('keydown',event=>{if(event.key==='Enter')finish(true);if(event.key==='Escape')finish(false);});input.addEventListener('blur',()=>finish(true));setTimeout(()=>{input.focus();input.select();},20);
  }
  function renameSectionInline(id){
    const section=state.sections[state.tab].find(item=>item.id===id),wrap=groups.querySelector(`[data-task-reveal-key="section:${CSS.escape(id)}"]`);if(!section||!wrap)return;closeReveal();const row=wrap.querySelector('[data-task-section-id]');[...row.children].forEach(child=>{if(!child.classList.contains('handle'))child.hidden=true;});
    const input=document.createElement('input');input.className='task-inline-input';input.value=sectionTitle(section);row.insertBefore(input,row.querySelector('.handle'));let done=false;
    const finish=save=>{if(done)return;done=true;const value=input.value.trim();if(save&&value){const previous=sectionTitle(section);if(section.projectId){const project=getProject(section.projectId);if(project)project.title=value;model.tasks.filter(task=>task.status===state.tab&&!linked(task)&&(task.section||'Личное')===previous).forEach(task=>task.section=value);}else{section.title=value;model.tasks.filter(task=>task.status===state.tab&&!linked(task)&&(task.section||'Личное')===previous).forEach(task=>task.section=value);}notify();}else render();};input.addEventListener('keydown',event=>{if(event.key==='Enter')finish(true);if(event.key==='Escape')finish(false);});input.addEventListener('blur',()=>finish(true));setTimeout(()=>{input.focus();input.select();},20);
  }
  function deleteSection(id){
    const section=state.sections[state.tab].find(item=>item.id===id);if(!section)return;
    const tasks=model.tasks.filter(task=>task.status===state.tab&&taskInSection(task,section)),name=sectionTitle(section),tab=state.tab;
    contextMenu.confirmAction({title:`Удалить раздел «${name}»?`,body:tasks.length?`${tasks.length} задач будут перемещены в архив.`:'Раздел будет удалён.',confirmLabel:'Удалить',onConfirm:()=>{
      tasks.forEach(task=>task.status='archived');state.sections[tab]=state.sections[tab].filter(item=>item.id!==id);state.expanded[tab].delete(id);state.revealed=null;notify();flash('Раздел удалён');
    }});
  }

  function currentTask(){return state.selectedId?getTask(state.selectedId):null;}
  function openTask(id){if(!getTask(id))return;finishEdits();state.orderMode=false;state.selectedId=id;board.hidden=true;detail.hidden=false;syncDetail();detail.scrollTop=0;}
  function closeTaskDetail(renderBoard=true){finishEdits();state.selectedId=null;detail.hidden=true;board.hidden=false;if(renderBoard)render();}
  function syncDetail(){
    const task=currentTask();if(!task){closeTaskDetail();return;}sectionLabel.textContent=task.status==='archived'?`Архив · ${originPath(task)}`:originPath(task);title.textContent=task.title;titleInput.value=task.title;description.textContent=task.description||'Описание пока не добавлено.';descriptionInput.value=task.description||'';moveButton.textContent=task.status==='archived'?'В Backlog':task.status==='backlog'?'В Сегодня':'В Backlog';statusButton.hidden=task.status!=='today';statusButton.textContent=workLabel(workState(task));statusButton.className=`task-detail-pill action-status ${workState(task)}`;deleteButton.hidden=task.status==='archived';title.hidden=state.titleEditing;titleInput.hidden=!state.titleEditing;description.hidden=state.descriptionEditing;descriptionInput.hidden=!state.descriptionEditing;
  }
  function startTitleEdit(){if(!currentTask()||state.titleEditing)return;finishDescriptionEdit();state.titleEditing=true;syncDetail();setTimeout(()=>{titleInput.focus();titleInput.select();},0);}
  function finishTitleEdit(save=true){if(!state.titleEditing)return;const task=currentTask(),value=titleInput.value.trim();state.titleEditing=false;if(save&&task&&value){task.title=value;notify();}else if(task)syncDetail();}
  function startDescriptionEdit(){if(!currentTask()||state.descriptionEditing)return;finishTitleEdit();state.descriptionEditing=true;syncDetail();setTimeout(()=>descriptionInput.focus(),0);}
  function finishDescriptionEdit(save=true){if(!state.descriptionEditing)return;const task=currentTask();state.descriptionEditing=false;if(save&&task){task.description=descriptionInput.value;notify();}else if(task)syncDetail();}
  function finishEdits(){finishTitleEdit();finishDescriptionEdit();}

  function updateCreate(){const taskMode=state.createType==='task';$('#taskTypeTaskFX').classList.toggle('active',taskMode);$('#taskTypeSectionFX').classList.toggle('active',!taskMode);$('#taskCreateTitleFX').textContent=taskMode?'Новая задача':'Новый раздел';$('#taskNameFX').placeholder=taskMode?'Название задачи':'Название раздела';$('#taskDescriptionCreateFX').hidden=!taskMode;$('#taskWhereFX').hidden=!taskMode;sectionList.hidden=!taskMode;}
  function renderCreateSections(){
    syncSections();if(!state.sections[state.tab].length)addSection(state.tab,{title:'Общее'});if(!state.createSectionId||!state.sections[state.tab].some(section=>section.id===state.createSectionId))state.createSectionId=state.sections[state.tab][0]?.id||'';
    const selected=state.sections[state.tab].find(section=>section.id===state.createSectionId);$('#taskWhereValueFX').textContent=selected?sectionTitle(selected):'Общее';sectionList.innerHTML='';
    for(const section of state.sections[state.tab]){const button=document.createElement('button');button.type='button';button.className=`parent-option ${state.createSectionId===section.id?'selected':''}`;button.textContent=sectionTitle(section);button.addEventListener('click',()=>{state.createSectionId=section.id;renderCreateSections();sectionList.classList.remove('open');});sectionList.append(button);}
  }
  function openCreate(){closeReveal();syncSections();state.createType='task';state.createSectionId=state.sections[state.tab][0]?.id||'';$('#taskNameFX').value='';$('#taskDescriptionCreateFX').value='';updateCreate();renderCreateSections();sectionList.classList.remove('open');overlay.classList.add('open');sheet.classList.add('open');setTimeout(()=>$('#taskNameFX').focus({preventScroll:true}),30);}
  function closeCreate(){if(document.activeElement&&sheet.contains(document.activeElement))document.activeElement.blur();sheet.classList.remove('open');overlay.classList.remove('open');sectionList.classList.remove('open');}

  function clearDragMarks(){groups.querySelectorAll('.task-drop-inside,.task-drop-before,.task-drop-after').forEach(item=>item.classList.remove('task-drop-inside','task-drop-before','task-drop-after'));}
  function clearGhost(){if(drag?.row)drag.row.classList.remove('task-drag-source');drag?.ghost?.remove();}
  function validTaskDrop(moved,target,place){if(!moved)return false;if(linked(moved)){if(place==='inside')return target?.projectId===moved.projectId;return linked(target)&&target.projectId===moved.projectId;}return true;}
  groups.addEventListener('pointerdown',event=>{
    if(event.button!==0||state.archive||!state.orderMode)return;const handle=event.target.closest('[data-task-drag-kind]');if(!handle)return;event.preventDefault();closeReveal();const kind=handle.dataset.taskDragKind,id=handle.dataset.taskDragId,row=kind==='task'?handle.closest('[data-task-id]'):handle.closest('[data-task-section-id]'),rect=row.getBoundingClientRect();drag={kind,id,pointerId:event.pointerId,startX:event.clientX,startY:event.clientY,row,target:null,place:null,started:false,ghost:null,offsetX:event.clientX-rect.left,offsetY:event.clientY-rect.top,width:rect.width,height:rect.height};handle.setPointerCapture?.(event.pointerId);
  },true);
  groups.addEventListener('pointermove',event=>{
    if(!drag||drag.pointerId!==event.pointerId)return;if(!drag.started&&Math.hypot(event.clientX-drag.startX,event.clientY-drag.startY)>6){drag.started=true;drag.row.classList.add('task-drag-source');const ghost=document.createElement('div');ghost.className='reorder-ghost';ghost.textContent=drag.kind==='task'?getTask(drag.id)?.title:sectionTitle(state.sections[state.tab].find(item=>item.id===drag.id));ghost.style.width=drag.width+'px';ghost.style.height=drag.height+'px';root.append(ghost);drag.ghost=ghost;}if(!drag.started)return;event.preventDefault();
    const bounds=$('#taskScrollFX').getBoundingClientRect(),pad=4;drag.ghost.style.left=Math.max(bounds.left+pad,Math.min(event.clientX-drag.offsetX,bounds.right-drag.width-pad))+'px';drag.ghost.style.top=Math.max(bounds.top+pad,Math.min(event.clientY-drag.offsetY,bounds.bottom-drag.height-pad))+'px';clearDragMarks();drag.target=null;drag.place=null;const hit=document.elementFromPoint(event.clientX,event.clientY);if(!hit)return;
    if(drag.kind==='task'){
      const taskRow=hit.closest?.('[data-task-id]');if(taskRow&&taskRow.dataset.taskId!==drag.id){const target=getTask(taskRow.dataset.taskId),moved=getTask(drag.id);if(!validTaskDrop(moved,target,'row'))return;const rect=taskRow.getBoundingClientRect();drag.target=taskRow;drag.place=event.clientY<rect.top+rect.height/2?'before':'after';taskRow.classList.add(drag.place==='before'?'task-drop-before':'task-drop-after');return;}
      const sectionRow=hit.closest?.('[data-task-section-id]');if(sectionRow){const section=state.sections[state.tab].find(item=>item.id===sectionRow.dataset.taskSectionId),moved=getTask(drag.id);if(!validTaskDrop(moved,section,'inside'))return;drag.target=sectionRow;drag.place='inside';sectionRow.classList.add('task-drop-inside');}
    }else{const sectionRow=hit.closest?.('[data-task-section-id]');if(!sectionRow||sectionRow.dataset.taskSectionId===drag.id)return;const rect=sectionRow.getBoundingClientRect();drag.target=sectionRow;drag.place=event.clientY<rect.top+rect.height/2?'before':'after';sectionRow.classList.add(drag.place==='before'?'task-drop-before':'task-drop-after');}
  },{passive:false});
  function finishDrag(event){
    if(!drag||drag.pointerId!==event.pointerId)return;const current=drag;clearGhost();clearDragMarks();drag=null;if(!current.started||!current.target)return;
    if(current.kind==='section'){const list=state.sections[state.tab],from=list.findIndex(section=>section.id===current.id),targetId=current.target.dataset.taskSectionId;if(from<0)return;const [moved]=list.splice(from,1),to=list.findIndex(section=>section.id===targetId);list.splice(to<0?list.length:to+(current.place==='after'?1:0),0,moved);render();return;}
    const moved=getTask(current.id);if(!moved)return;
    if(current.place==='inside'){
      const destination=state.sections[state.tab].find(section=>section.id===current.target.dataset.taskSectionId);if(!destination||!validTaskDrop(moved,destination,'inside'))return;if(!linked(moved))moved.section=sectionTitle(destination);const from=model.tasks.indexOf(moved);model.tasks.splice(from,1);let last=-1;model.tasks.forEach((task,index)=>{if(task.status===state.tab&&taskInSection(task,destination))last=index;});model.tasks.splice(last+1,0,moved);state.expanded[state.tab].add(destination.id);
    }else{
      const target=getTask(current.target.dataset.taskId);if(!target||!validTaskDrop(moved,target,'row'))return;if(!linked(moved))moved.section=linked(target)?projectTitle(target.projectId):(target.section||'Личное');const from=model.tasks.indexOf(moved);model.tasks.splice(from,1);const targetIndex=model.tasks.indexOf(target);model.tasks.splice(targetIndex+(current.place==='after'?1:0),0,moved);const destination=linked(target)?sectionForKey(state.tab,`project:${target.projectId}`):sectionForKey(state.tab,`plain:${target.section||'Личное'}`);if(destination)state.expanded[state.tab].add(destination.id);
    }
    notify();
  }
  groups.addEventListener('pointerup',finishDrag,true);groups.addEventListener('pointercancel',event=>{if(!drag||drag.pointerId!==event.pointerId)return;clearGhost();clearDragMarks();drag=null;},true);

  $('#taskBackFX').addEventListener('click',()=>closeTaskDetail());title.addEventListener('click',startTitleEdit);titleInput.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();finishTitleEdit();}else if(event.key==='Escape'){event.preventDefault();finishTitleEdit(false);}});titleInput.addEventListener('blur',()=>finishTitleEdit());description.addEventListener('click',startDescriptionEdit);descriptionInput.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();finishDescriptionEdit(false);}});descriptionInput.addEventListener('blur',()=>finishDescriptionEdit());moveButton.addEventListener('click',()=>{const task=currentTask();if(task)moveTask(task.id,task.status==='backlog'?'today':'backlog');});statusButton.addEventListener('click',()=>{const task=currentTask();if(task)advanceStatus(task.id);});deleteButton.addEventListener('click',()=>{const task=currentTask();if(task)archiveTask(task.id);});
  root.querySelectorAll('[data-task-tab]').forEach(button=>button.addEventListener('click',()=>{finishEdits();closeReveal();state.orderMode=false;state.archive=false;state.tab=button.dataset.taskTab;state.filter='all';closeTaskDetail(false);render();}));root.querySelectorAll('[data-task-filter]').forEach(button=>button.addEventListener('click',()=>{state.filter=button.dataset.taskFilter;closeReveal();render();}));archiveOpen.addEventListener('click',()=>{finishEdits();closeReveal();state.orderMode=false;state.archive=true;closeTaskDetail(false);render();});archiveBack.addEventListener('click',()=>{finishEdits();closeReveal();state.orderMode=false;state.archive=false;closeTaskDetail(false);render();});toggleAll.addEventListener('click',()=>{const ids=[...groups.querySelectorAll('[data-task-section-id]')].map(row=>row.dataset.taskSectionId),allOpen=ids.length>0&&ids.every(id=>state.expanded[state.tab].has(id));if(allOpen)ids.forEach(id=>state.expanded[state.tab].delete(id));else ids.forEach(id=>state.expanded[state.tab].add(id));render();});
  orderToggle.addEventListener('click',()=>{closeReveal();swipe=null;clearGhost();clearDragMarks();drag=null;state.orderMode=!state.orderMode;render();});
  add.addEventListener('click',openCreate);$('#taskCreateCloseFX').addEventListener('click',closeCreate);$('#taskTypeTaskFX').addEventListener('click',()=>{state.createType='task';updateCreate();renderCreateSections();});$('#taskTypeSectionFX').addEventListener('click',()=>{state.createType='section';updateCreate();});$('#taskWhereFX').addEventListener('click',()=>sectionList.classList.toggle('open'));
  $('#taskCreateSubmitFX').addEventListener('click',()=>{const taskName=$('#taskNameFX').value.trim();if(!taskName)return;if(state.createType==='section'){const section=addSection(state.tab,{title:taskName});state.expanded[state.tab].add(section.id);closeCreate();render();flash('Раздел создан');return;}const section=state.sections[state.tab].find(item=>item.id===state.createSectionId)||addSection(state.tab,{title:'Общее'});const task={id:model.nextId('task'),title:taskName,description:$('#taskDescriptionCreateFX').value.trim(),section:sectionTitle(section),status:state.tab,workStatus:'new',completed:false};model.tasks.push(task);state.expanded[state.tab].add(section.id);closeCreate();notify();openTask(task.id);});
  overlay.addEventListener('click',()=>{if(sheet.classList.contains('open'))closeCreate();});
  document.addEventListener('personalos:pagechange',event=>{const active=event.detail.page==='tasks';view.hidden=!active;if(active){state.orderMode=false;document.getElementById('placeholderFX').hidden=true;board.hidden=false;detail.hidden=true;state.selectedId=null;render();}else{state.orderMode=false;clearGhost();clearDragMarks();drag=null;closeCreate();closeReveal();}});
  document.addEventListener('personalos:opentask',event=>{if(getTask(event.detail.id))openTask(event.detail.id);});
  model.subscribe(()=>{syncSections();if(state.selectedId){if(getTask(state.selectedId))syncDetail();else closeTaskDetail();}else render();});
  syncSections();render();
})();
