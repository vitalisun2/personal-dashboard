(()=>{
  const model=window.personalOS,section=document.getElementById('planningFX');
  if(!model||!section)return;
  const contextMenu=window.personalOSContextMenu;
  const toolbar=document.getElementById('planningToolbarFX'),scroll=document.getElementById('planningScrollFX');
  const placeholder=document.getElementById('placeholderFX'),overlay=document.getElementById('overlayFX'),sheet=document.getElementById('planningSheetFX');
  const sheetTitle=document.getElementById('planningSheetTitleFX'),sheetContext=document.getElementById('planningSheetContextFX');
  const nameInput=document.getElementById('planningNameFX'),descriptionInput=document.getElementById('planningDescriptionFX'),descriptionLabel=document.getElementById('planningDescriptionLabelFX');
  const saveButton=document.getElementById('planningSheetSaveFX'),deleteButton=document.getElementById('planningSheetDeleteFX');
  const state={screen:'roadmap',projectId:model.projects[0]?.id||null,milestoneId:null,featureId:null,taskId:null,menuOpen:false,editor:null,sheetScroll:0,editing:null,orderMode:false,dragCleanup:null};
  const safe=value=>String(value??'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  const projects=()=>model.projects.filter(item=>!item.archived);
  function project(){let item=model.getProject(state.projectId);if(item&&!item.archived)return item;item=projects()[0]||null;state.projectId=item?.id||null;return item}
  const milestone=()=>project()?.milestones.find(item=>item.id===state.milestoneId)||null;
  const feature=()=>milestone()?.features.find(item=>item.id===state.featureId)||null;
  const task=()=>model.getTask(state.taskId);
  const featureTasks=item=>model.tasks.filter(task=>task.featureId===item?.id);
  const percent=value=>Math.max(0,Math.min(100,Math.round(value)||0));
  const featureProgress=item=>{const tasks=featureTasks(item);return tasks.length?percent(tasks.filter(task=>task.completed).length/tasks.length*100):0};
  const milestoneProgress=item=>item.features.length?percent(item.features.reduce((sum,feature)=>sum+featureProgress(feature),0)/item.features.length):0;
  const projectProgress=item=>item.milestones.length?percent(item.milestones.reduce((sum,milestone)=>sum+milestoneProgress(milestone),0)/item.milestones.length):0;
  const completedFeatures=item=>item.features.filter(feature=>{const tasks=featureTasks(feature);return tasks.length&&tasks.every(task=>task.completed)}).length;
  const track=value=>`<div class="planning-progress-track"><div class="planning-progress-fill" style="width:${percent(value)}%"></div></div>`;
  const trash=()=>'<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 4.75h6m-8.5 2.5h10m-8.5 0-.35 10.1c-.03.84.64 1.55 1.48 1.55h4.74c.84 0 1.51-.71 1.48-1.55l-.35-10.1M10.25 10v5.5M13.75 10v5.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const orderAction=()=>`<button class="order-mode-toggle planning-order-toggle" type="button" data-action="toggle-order" aria-label="${state.orderMode?'Выключить':'Включить'} сортировку" aria-pressed="${state.orderMode}" title="${state.orderMode?'Выключить':'Включить'} сортировку"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5" /></svg></button>`;
  const dragHandle=()=>'<span class="planning-order-handle handle" aria-hidden="true"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></span>';
  const menuTrigger=(kind,item,extra='')=>`<button class="planning-menu-trigger ${extra}" type="button" data-action="open-context-menu" data-kind="${kind}" data-id="${safe(item.id)}" aria-label="Действия с «${safe(item.title)}»" aria-haspopup="menu" aria-expanded="false" title="Действия">⋯</button>`;
  const chevronSlot=(kind,item)=>`<span class="planning-chevron-slot" data-action="open-${kind}" data-id="${safe(item.id)}"><span class="planning-row-arrow" aria-hidden="true">›</span>${menuTrigger(kind,item)}</span>`;
  function statusMeta(item){
    if(item.status==='planned')return null;
    if(item.completed)return{label:'Готово',kind:'completed'};
    if(item.status==='today'&&item.workStatus==='active')return{label:'В работе',kind:'in_progress'};
    if(item.status==='today')return{label:'Сегодня',kind:'today'};
    if(item.status==='archived')return{label:'В архиве',kind:'archived'};
    return{label:'Backlog',kind:'backlog'};
  }
  function renderToolbar(){
    toolbar.hidden=state.screen!=='roadmap';if(toolbar.hidden)return;
    const item=project();
    toolbar.innerHTML=`<div class="planning-picker-wrap"><button class="planning-picker" type="button" data-action="picker" aria-expanded="${state.menuOpen}"><span>${safe(item?.title||'Нет проектов')}</span><span class="planning-picker-arrow">⌄</span></button><div class="planning-picker-menu ${state.menuOpen?'open':''}">${projects().map(entry=>`<div class="planning-picker-option ${entry.id===item?.id?'active':''}"><button type="button" data-action="select-project" data-id="${safe(entry.id)}">${safe(entry.title)}</button><button type="button" class="planning-picker-edit" data-action="edit-project" data-id="${safe(entry.id)}" aria-label="Редактировать ${safe(entry.title)}">✎</button></div>`).join('')}</div></div><button class="plus" type="button" data-action="create-project" aria-label="Добавить проект">＋</button>`;
  }
  function renderRoadmap(){
    const item=project();if(!item){scroll.innerHTML='<div class="planning-empty">Нет проектов. Нажмите ＋, чтобы добавить новый проект.</div>';return}
    const progress=projectProgress(item);
    scroll.innerHTML=`<div class="planning-project-summary"><div class="planning-summary-top"><span>Прогресс проекта</span><strong>${progress}%</strong></div>${track(progress)}</div><div class="planning-list-head"><span>Вехи</span><div class="planning-list-actions">${orderAction()}<button type="button" data-action="create-milestone" aria-label="Добавить веху">＋</button></div></div><div class="planning-roadmap">${item.milestones.map(entry=>state.orderMode?`<div class="planning-milestone-row planning-order-row" data-order-id="${safe(entry.id)}"><span class="planning-milestone-title">${safe(entry.title)}</span><span class="planning-milestone-progress"><strong>${milestoneProgress(entry)}%</strong>${track(milestoneProgress(entry))}</span>${dragHandle()}</div>`:`<div class="planning-milestone-row planning-context-row" data-context-kind="milestone" data-context-id="${safe(entry.id)}"><button class="planning-row-main" type="button" data-action="open-milestone" data-id="${safe(entry.id)}"><span class="planning-milestone-title">${safe(entry.title)}</span><span class="planning-milestone-progress"><strong>${milestoneProgress(entry)}%</strong>${track(milestoneProgress(entry))}</span></button>${chevronSlot('milestone',entry)}</div>`).join('')}</div>`;
  }
  function renderMilestone(){
    const item=milestone(),p=project();if(!item||!p){state.screen='roadmap';render();return}
    const progress=milestoneProgress(item);
    scroll.innerHTML=`<button class="doc-action doc-back planning-inline-back" type="button" data-action="back-roadmap">← Назад</button><div class="planning-head-card"><div class="planning-kicker">${safe(p.title)}</div><div class="planning-head-main"><button class="planning-head-title" type="button" data-action="edit-milestone">${safe(item.title)}</button><div class="planning-head-actions">${orderAction()}<button class="planning-head-plus" type="button" data-action="create-feature" aria-label="Добавить фичу">＋</button></div></div><div class="planning-milestone-progress-block"><div class="planning-progress-top"><span>Прогресс вехи</span><strong>${completedFeatures(item)} из ${item.features.length} фич</strong></div>${track(progress)}</div></div><div class="planning-feature-list">${item.features.map(entry=>state.orderMode?`<div class="planning-feature-row planning-order-row" data-order-id="${safe(entry.id)}"><span>${safe(entry.title)}</span>${dragHandle()}</div>`:`<div class="planning-feature-row planning-context-row" data-context-kind="feature" data-context-id="${safe(entry.id)}"><button class="planning-row-main" type="button" data-action="open-feature" data-id="${safe(entry.id)}"><span>${safe(entry.title)}</span></button>${chevronSlot('feature',entry)}</div>`).join('')}</div>`;
  }
  function renderFeature(){
    const item=feature(),m=milestone(),p=project();if(!item||!m||!p){state.screen='milestone';render();return}
    const tasks=featureTasks(item);
    scroll.innerHTML=`<button class="doc-action doc-back planning-inline-back" type="button" data-action="back-milestone">← Назад</button><div class="planning-head-card planning-feature-head"><div class="planning-kicker">${safe(p.title)} · ${safe(m.title)}</div><div class="planning-head-main"><button class="planning-head-title" type="button" data-action="edit-feature">${safe(item.title)}</button><span class="planning-task-count" aria-label="${tasks.length} задач">${tasks.length}</span></div></div><div class="planning-list-head planning-feature-list-head"><span>Задачи</span><div class="planning-list-actions">${orderAction()}<button type="button" data-action="create-task" aria-label="Добавить задачу">＋</button></div></div><div class="planning-feature-tasks">${tasks.map(entry=>{const meta=statusMeta(entry);return state.orderMode?`<div class="planning-feature-task planning-order-row" data-order-id="${safe(entry.id)}"><span class="planning-feature-task-title">${safe(entry.title)}</span>${dragHandle()}</div>`:`<div class="planning-feature-task planning-context-row" data-context-kind="task" data-context-id="${safe(entry.id)}"><button class="planning-feature-task-title" type="button" data-action="open-task" data-id="${safe(entry.id)}">${safe(entry.title)}</button>${meta?`<button class="planning-task-state ${meta.kind}" type="button" data-action="open-in-tasks" data-id="${safe(entry.id)}" aria-label="Открыть в задачах: ${safe(entry.title)}">${meta.label}</button>`:`<button class="planning-send-backlog" type="button" data-action="send-backlog" data-id="${safe(entry.id)}">Backlog</button>`}${menuTrigger('task',entry,'planning-task-menu-trigger')}</div>`}).join('')}</div>`;
  }
  function renderTask(){
    const item=task(),f=feature(),m=milestone(),p=project();if(!item||!f||!m||!p){state.screen='feature';render();return}
    const meta=statusMeta(item);
    scroll.innerHTML=`<button class="doc-action doc-back planning-inline-back" type="button" data-action="back-feature">← Назад</button><div class="planning-task-context">${safe(p.title)} · ${safe(m.title)} · ${safe(f.title)}</div><button class="planning-detail-title" type="button" data-action="edit-task-title">${safe(item.title)}</button><input class="planning-detail-title-input" type="text" maxlength="120" hidden /><div class="planning-description-card"><button class="planning-detail-description" type="button" data-action="edit-task-description">${safe(item.description||'Описание пока не добавлено.')}</button><textarea class="planning-detail-description-input" hidden></textarea></div><div class="planning-task-actions"><span></span><div class="planning-task-center">${meta?`<button class="planning-task-detail-status ${meta.kind}" type="button" data-action="open-in-tasks" data-id="${safe(item.id)}" aria-label="Открыть задачу в Task Tracker">${meta.label==='Backlog'?'В бэклоге':meta.label}</button>`:`<button class="planning-task-backlog" type="button" data-action="send-backlog" data-id="${safe(item.id)}">Backlog</button>`}</div><button class="planning-task-delete" type="button" data-action="delete-task" data-id="${safe(item.id)}" aria-label="Удалить задачу">${trash()}</button></div>`;
  }
  function render(keepScroll=false){const top=keepScroll?scroll.scrollTop:0;if(section.querySelector('.planning-context-row.context-active'))contextMenu.close();renderToolbar();({roadmap:renderRoadmap,milestone:renderMilestone,feature:renderFeature,task:renderTask})[state.screen]();scroll.scrollTop=top}
  function navigate(screen,id=null){state.dragCleanup?.(false);finishEdit(true);state.orderMode=false;state.screen=screen;if(screen==='roadmap'){state.milestoneId=null;state.featureId=null;state.taskId=null}else if(screen==='milestone'){if(id)state.milestoneId=id;state.featureId=null;state.taskId=null}else if(screen==='feature'){if(id)state.featureId=id;state.taskId=null}else if(screen==='task')state.taskId=id;state.menuOpen=false;render()}
  function commitOrder(){
    const rows=[...scroll.querySelectorAll('.planning-order-row')],ids=rows.map(row=>row.dataset.orderId);if(ids.length<2)return;
    let list;if(state.screen==='roadmap')list=project()?.milestones;else if(state.screen==='milestone')list=milestone()?.features;else list=model.tasks;
    if(!list)return;
    const selected=state.screen==='feature'?new Set(ids):null;
    if(selected){const ordered=ids.map(id=>model.getTask(id));let at=0;for(let i=0;i<list.length;i++)if(selected.has(list[i].id))list[i]=ordered[at++];}
    else{const byId=new Map(list.map(item=>[item.id,item]));ids.forEach((id,index)=>{const item=byId.get(id);if(item)list[index]=item})}
    model.notify();
  }
  function startDrag(event){
    const handle=event.target.closest('.planning-order-handle');if(!handle||!state.orderMode||state.dragCleanup||event.button>0)return;
    const row=handle.closest('.planning-order-row');if(!row)return;event.preventDefault();
    const rect=row.getBoundingClientRect(),ghost=row.cloneNode(true);ghost.classList.add('planning-order-ghost','reorder-ghost');ghost.style.width=`${rect.width}px`;ghost.style.height=`${rect.height}px`;section.append(ghost);row.classList.add('planning-order-source');
    const pointerId=event.pointerId,offsetX=rect.left-event.clientX,offsetY=rect.top-event.clientY;
    const move=e=>{if(e.pointerId!==pointerId)return;const bounds=scroll.getBoundingClientRect(),pad=4,x=Math.max(bounds.left+pad,Math.min(e.clientX+offsetX,bounds.right-rect.width-pad)),y=Math.max(bounds.top+pad,Math.min(e.clientY+offsetY,bounds.bottom-rect.height-pad));ghost.style.left=`${x}px`;ghost.style.top=`${y}px`;const target=[...scroll.querySelectorAll('.planning-order-row')].filter(candidate=>candidate!==row).find(candidate=>{const r=candidate.getBoundingClientRect();return e.clientY<r.top+r.height/2});if(target)target.before(row);else scroll.querySelector('.planning-order-row:last-child')?.after(row)};
    const cleanup=(commit)=>{if(!state.dragCleanup)return;window.removeEventListener('pointermove',move,true);window.removeEventListener('pointerup',up,true);window.removeEventListener('pointercancel',cancel,true);window.removeEventListener('blur',blur,true);state.dragCleanup=null;ghost.remove();row.classList.remove('planning-order-source');if(commit)commitOrder();else render(true)};
    const up=e=>{if(e.pointerId===pointerId)cleanup(true)};
    const cancel=e=>{if(e.pointerId===pointerId)cleanup(false)};
    const blur=()=>cleanup(false);
    state.dragCleanup=cleanup;window.addEventListener('pointermove',move,true);window.addEventListener('pointerup',up,true);window.addEventListener('pointercancel',cancel,true);window.addEventListener('blur',blur,true);
  }
  function openEditor(type,id=null){
    if((type==='milestone'&&!project())||(type==='feature'&&!milestone())||(type==='task'&&!feature()))return;
    state.editor={type,id};state.sheetScroll=scroll.scrollTop;state.menuOpen=false;renderToolbar();
    const names={project:'проект',milestone:'веху',feature:'фичу',task:'задачу'};
    sheetTitle.textContent=id?`Изменить ${names[type]}`:({project:'Новый проект',milestone:'Новая веха',feature:'Новая фича',task:'Новая задача'})[type];
    nameInput.placeholder=type==='project'?'Название проекта':type==='milestone'?'Название вехи':type==='feature'?'Название фичи':'Название задачи';
    const target=id?(type==='project'?model.getProject(id):type==='milestone'?milestone():type==='feature'?feature():task()):null;
    nameInput.value=target?.title||'';descriptionInput.value=target?.description||'';
    const showDescription=type==='task'||Boolean(id);descriptionInput.hidden=!showDescription;descriptionLabel.hidden=!showDescription;sheetContext.hidden=true;
    saveButton.textContent=id?'Сохранить':type==='task'?'Создать задачу':'Создать';deleteButton.hidden=!id;
    overlay.classList.add('open');sheet.classList.add('open');
  }
  function closeEditor(){state.editor=null;sheet.classList.remove('open');overlay.classList.remove('open');if(sheet.contains(document.activeElement))document.activeElement.blur();scroll.scrollTop=state.sheetScroll}
  function saveEditor(){
    const editor=state.editor;if(!editor)return;const title=nameInput.value.trim(),description=descriptionInput.value.trim();if(!title)return;
    if(editor.id){const target=editor.type==='project'?model.getProject(editor.id):editor.type==='milestone'?milestone():editor.type==='feature'?feature():task();if(!target)return;target.title=title;target.description=description}
    else if(editor.type==='project'){const item={id:model.nextId('project'),title,description,milestones:[]};model.projects.push(item);state.projectId=item.id}
    else if(editor.type==='milestone')project().milestones.push({id:model.nextId('milestone'),title,description,features:[]});
    else if(editor.type==='feature')milestone().features.push({id:model.nextId('feature'),title,description,status:'planned'});
    else model.tasks.push({id:model.nextId('task'),title,description,projectId:state.projectId,milestoneId:state.milestoneId,featureId:state.featureId,status:'planned',completed:false});
    const top=state.sheetScroll;closeEditor();render();scroll.scrollTop=top;model.notify();
  }
  function detachTasks(predicate,title){
    for(let i=model.tasks.length-1;i>=0;i--){const item=model.tasks[i];if(!predicate(item))continue;
      if(item.status==='planned')model.tasks.splice(i,1);
      else{item.section=title;delete item.projectId;delete item.milestoneId;delete item.featureId}
    }
  }
  function deleteEntity(type=state.editor?.type,id=state.editor?.id){
    if(!id)return;
    if(type==='task'){deleteTask(id);return}
    const p=project(),m=milestone();
    const item=type==='project'?model.getProject(id):type==='milestone'?p?.milestones.find(entry=>entry.id===id):m?.features.find(entry=>entry.id===id);
    if(!item)return;
    contextMenu.confirmAction({
      title:`Удалить «${item.title}»?`,
      body:'Плановые задачи будут удалены. Задачи из Backlog и Сегодня останутся в разделе проекта.',
      confirmLabel:'Удалить',
      onConfirm:()=>{
        if(type==='project'){detachTasks(task=>task.projectId===item.id,item.title);model.projects.splice(model.projects.indexOf(item),1);state.projectId=projects()[0]?.id||null;state.screen='roadmap'}
        else if(type==='milestone'){detachTasks(task=>task.milestoneId===item.id,p.title);p.milestones.splice(p.milestones.indexOf(item),1);state.screen='roadmap'}
        else{detachTasks(task=>task.featureId===item.id,p.title);m.features.splice(m.features.indexOf(item),1);state.screen='milestone'}
        if(state.editor)closeEditor();render();model.notify();
      }
    });
  }
  function deleteTask(id){
    const item=model.getTask(id);if(!item)return;const sent=item.status!=='planned';
    contextMenu.confirmAction({
      title:`Удалить «${item.title}» из планирования?`,
      body:sent?'Задача останется в Task Tracker без связи с планом.':'Задача будет удалена из плана.',
      confirmLabel:'Удалить',
      onConfirm:()=>{
        if(sent){item.section=project()?.title||'Проект';delete item.projectId;delete item.milestoneId;delete item.featureId}
        else model.tasks.splice(model.tasks.indexOf(item),1);
        if(state.editor)closeEditor();state.screen='feature';state.taskId=null;render();model.notify();
      }
    });
  }
  function menuItems(kind,id){
    if(kind==='milestone')return[
      {label:'Открыть веху',action:()=>navigate('milestone',id)},
      {label:'Изменить',action:()=>openEditor('milestone',id)},
      {label:'Удалить веху',danger:true,action:()=>deleteEntity('milestone',id)}
    ];
    if(kind==='feature')return[
      {label:'Открыть фичу',action:()=>navigate('feature',id)},
      {label:'Изменить',action:()=>openEditor('feature',id)},
      {label:'Удалить фичу',danger:true,action:()=>deleteEntity('feature',id)}
    ];
    const item=model.getTask(id);if(!item)return[];
    const items=[
      {label:'Открыть задачу',action:()=>navigate('task',id)},
      {label:'Изменить',action:()=>openEditor('task',id)}
    ];
    if(item.status==='planned')items.push({label:'В Backlog',action:()=>sendToBacklog(id)});
    else{
      items.push({label:'Открыть в задачах',action:()=>openInTasks(id)});
      if(item.status==='backlog'&&item.projectId&&item.featureId)items.push({label:'Вернуть в план',action:()=>returnToPlan(id)});
    }
    items.push({label:'Удалить задачу',danger:true,action:()=>deleteTask(id)});
    return items;
  }
  function showPlanningMenu(row,point=null,trigger=null){
    if(state.orderMode)return;
    const {contextKind:kind,contextId:id}=row.dataset;
    const item=kind==='milestone'?project()?.milestones.find(entry=>entry.id===id):kind==='feature'?milestone()?.features.find(entry=>entry.id===id):model.getTask(id);
    if(!item)return;
    contextMenu.open({owner:row,anchor:row,items:menuItems(kind,id),point,trigger,label:`Действия с «${item.title}»`});
  }
  function sendToBacklog(id){model.moveTaskToBacklog(id)}
  function returnToPlan(id){model.moveTaskToPlan(id)}
  function openInTasks(id){if(!model.getTask(id))return;document.querySelector('#bottomNav [data-page="tasks"]')?.click();document.dispatchEvent(new CustomEvent('personalos:opentask',{detail:{id}}))}
  function startEdit(kind){
    if(state.screen!=='task'||!task())return;finishEdit(true);
    const button=scroll.querySelector(kind==='title'?'.planning-detail-title':'.planning-detail-description');
    const input=scroll.querySelector(kind==='title'?'.planning-detail-title-input':'.planning-detail-description-input');
    if(!button||!input)return;state.editing={kind,button,input};button.hidden=true;input.hidden=false;
    input.value=kind==='title'?task().title:task().description||'';input.focus({preventScroll:true});if(kind==='title')input.select();
  }
  function finishEdit(save){
    const editing=state.editing;if(!editing)return;state.editing=null;
    const item=task(),value=editing.input.value.trim();
    if(item&&save){if(editing.kind==='title'&&value)item.title=value;if(editing.kind==='description')item.description=value}
    editing.input.hidden=true;editing.button.hidden=false;
    if(item){editing.button.textContent=editing.kind==='title'?item.title:item.description||'Описание пока не добавлено.';if(save)model.notify()}
  }
  section.addEventListener('click',event=>{
    const button=event.target.closest('[data-action]');
    if(!button||!section.contains(button)){if(state.menuOpen&&!event.target.closest('.planning-picker-wrap')){state.menuOpen=false;renderToolbar()}return}
    const {action,id}=button.dataset;
    if(action==='open-context-menu'){
      const row=button.closest('.planning-context-row');
      if(row){event.stopPropagation();contextMenu.isOpenFor(row)?contextMenu.close():showPlanningMenu(row,null,button)}
    }
    else if(action==='toggle-order'){state.orderMode=!state.orderMode;render(true)}
    else if(action==='picker'){state.menuOpen=!state.menuOpen;renderToolbar()}
    else if(action==='select-project'){state.projectId=id;state.menuOpen=false;state.orderMode=false;render()}
    else if(action==='edit-project')openEditor('project',id);
    else if(action==='create-project')openEditor('project');
    else if(action==='create-milestone')openEditor('milestone');
    else if(action==='open-milestone')navigate('milestone',id);
    else if(action==='back-roadmap')navigate('roadmap');
    else if(action==='edit-milestone')openEditor('milestone',state.milestoneId);
    else if(action==='create-feature')openEditor('feature');
    else if(action==='open-feature')navigate('feature',id);
    else if(action==='back-milestone')navigate('milestone');
    else if(action==='edit-feature')openEditor('feature',state.featureId);
    else if(action==='create-task')openEditor('task');
    else if(action==='open-task')navigate('task',id);
    else if(action==='back-feature')navigate('feature');
    else if(action==='send-backlog')sendToBacklog(id);
    else if(action==='open-in-tasks')openInTasks(id);
    else if(action==='edit-task-title')startEdit('title');
    else if(action==='edit-task-description')startEdit('description');
    else if(action==='delete-task')deleteTask(id);
  });
  let planningSwipe=null,suppressPlanningClickUntil=0;
  const cancelSwipeTimer=()=>{if(planningSwipe?.timer){clearTimeout(planningSwipe.timer);planningSwipe.timer=null}};
  scroll.addEventListener('pointerdown',event=>{
    const row=event.target.closest('.planning-context-row');
    if(!row||state.orderMode||event.button>0||event.target.closest('.planning-menu-trigger'))return;
    cancelSwipeTimer();
    const current={row,pointerId:event.pointerId,startX:event.clientX,startY:event.clientY,moved:false,long:false,timer:null};
    planningSwipe=current;
    if(event.pointerType==='touch'||event.pointerType==='pen')current.timer=setTimeout(()=>{
      if(planningSwipe!==current||current.moved)return;
      current.long=true;suppressPlanningClickUntil=Date.now()+400;showPlanningMenu(row);
    },480);
    event.target.setPointerCapture?.(event.pointerId);
  });
  scroll.addEventListener('pointermove',event=>{
    const current=planningSwipe;if(!current||current.pointerId!==event.pointerId)return;
    const dx=event.clientX-current.startX,dy=event.clientY-current.startY;
    if(Math.hypot(dx,dy)>8){current.moved=true;cancelSwipeTimer()}
    if(Math.abs(dy)>Math.abs(dx)&&Math.abs(dy)>10){planningSwipe=null;return}
    if(dx<-12&&Math.abs(dx)>Math.abs(dy)*1.1)event.preventDefault();
  },{passive:false});
  scroll.addEventListener('pointerup',event=>{
    const current=planningSwipe;if(!current||current.pointerId!==event.pointerId)return;
    cancelSwipeTimer();planningSwipe=null;
    if(current.long)return;
    const dx=event.clientX-current.startX,dy=event.clientY-current.startY;
    if(Math.abs(dx)<Math.abs(dy)*1.2)return;
    if(dx<=-56){suppressPlanningClickUntil=Date.now()+400;showPlanningMenu(current.row)}
    else if(dx>=45&&contextMenu.isOpenFor(current.row)){suppressPlanningClickUntil=Date.now()+400;contextMenu.close()}
  });
  scroll.addEventListener('pointercancel',event=>{if(planningSwipe?.pointerId===event.pointerId){cancelSwipeTimer();planningSwipe=null}});
  scroll.addEventListener('contextmenu',event=>{
    const row=event.target.closest('.planning-context-row');if(!row||state.orderMode)return;
    event.preventDefault();if(!contextMenu.isOpenFor(row))showPlanningMenu(row,{x:event.clientX,y:event.clientY});
  });
  scroll.addEventListener('click',event=>{if(Date.now()<suppressPlanningClickUntil){event.preventDefault();event.stopPropagation()}},true);
  scroll.addEventListener('pointerdown',startDrag);
  scroll.addEventListener('focusout',event=>{if(state.editing?.input===event.target)finishEdit(true)});
  scroll.addEventListener('keydown',event=>{if(state.editing?.input!==event.target)return;if(event.key==='Escape'){event.preventDefault();finishEdit(false)}else if(event.key==='Enter'&&state.editing.kind==='title'){event.preventDefault();finishEdit(true)}});
  document.getElementById('planningSheetCloseFX').addEventListener('click',closeEditor);
  saveButton.addEventListener('click',saveEditor);deleteButton.addEventListener('click',()=>deleteEntity());
  overlay.addEventListener('click',()=>{if(state.editor)closeEditor()});
  nameInput.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();saveEditor()}else if(event.key==='Escape'){event.preventDefault();closeEditor()}});
  descriptionInput.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();closeEditor()}});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&state.editor)closeEditor()});
  document.addEventListener('personalos:pagechange',event=>{const active=event.detail.page==='planning';section.hidden=!active;if(active){state.dragCleanup?.(false);placeholder.hidden=true;state.screen='roadmap';state.orderMode=false;state.menuOpen=false;render()}else{state.dragCleanup?.(false);state.orderMode=false;state.menuOpen=false;finishEdit(true);if(state.editor)closeEditor()}});
  model.subscribe(()=>{if(!section.hidden&&!state.editor&&!state.editing)render(true)});
  render();
})();
