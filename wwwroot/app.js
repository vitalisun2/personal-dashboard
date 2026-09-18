(() => {
  const state = {
    main: 'tasks', tasks: [], taskTab: 'backlog', taskFilter: 'all', selectedTaskId: null,
    expanded: { backlog: new Set(), today: new Set() },
    dashboard: null, memoryStatus: null, memoryTab: 'overview', secondary: 'agents', selectedLesson: null
  };

  const $ = s => document.querySelector(s);
  const $$ = s => [...document.querySelectorAll(s)];
  const toast = $('#toast');
  const statusLabel = { new: 'В работу', in_progress: 'Завершить', completed: 'Стикер' };

  function showToast(message) {
    toast.textContent = message; toast.classList.remove('hidden');
    clearTimeout(showToast.timer); showToast.timer = setTimeout(() => toast.classList.add('hidden'), 1800);
  }

  async function api(url, options = {}) {
    const response = await fetch(url, { ...options, headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } });
    if (!response.ok) { let m='Ошибка запроса.'; try { m=(await response.json()).message||m; } catch {} throw new Error(m); }
    return response.status === 204 ? null : response.json();
  }

  const normalizeSection = s => String(s || '').trim() || 'Общее';
  const statusDotClass = s => s === 'in_progress' ? 'work' : s === 'completed' ? 'done' : 'new';

  // -------- Main navigation --------
  function setMain(value) {
    state.main = value;
    $('#tasksSection').classList.toggle('hidden', value !== 'tasks');
    $('#memorySection').classList.toggle('hidden', value !== 'memory');
    $$('[data-main]').forEach(b => b.classList.toggle('active', b.dataset.main === value));
  }

  // -------- Tasks --------
  async function loadTasks() {
    state.tasks = (await api('/api/tasks')).map(t => ({ ...t, section: normalizeSection(t.section) }));
    renderTasks();
  }

  function taskVisible(t) {
    if (t.bucket !== state.taskTab) return false;
    return state.taskTab !== 'today' || state.taskFilter === 'all' || t.status === state.taskFilter;
  }

  function createTaskRow(task) {
    const row = document.createElement('article'); row.className = 'task-row' + (task.bucket === 'today' ? ' today' : '');
    const open = document.createElement('button'); open.type='button'; open.className='task-open'; open.dataset.taskOpen=task.id;
    if (task.bucket === 'today') { const dot=document.createElement('span'); dot.className=`status-dot ${statusDotClass(task.status)}`; open.append(dot); }
    const title=document.createElement('span'); title.className='task-title'; title.textContent=task.title; open.append(title);
    const move=document.createElement('button'); move.type='button'; move.className='move-button'; move.dataset.taskMove=task.id; move.textContent=task.bucket==='backlog'?'→':'←';
    row.append(open, move);
    if (task.bucket === 'today') { const b=document.createElement('button'); b.type='button'; b.className='status-button'; b.dataset.taskAdvance=task.id; b.textContent=statusLabel[task.status]; row.append(b); }
    return row;
  }

  function renderTasks() {
    const today = state.taskTab === 'today';
    $('#pageTitle').textContent = today ? 'Сегодня' : 'Backlog';
    $('#backlogTab').classList.toggle('active', !today); $('#todayTab').classList.toggle('active', today);
    $('#addForm').classList.toggle('hidden', today); $('#filters').classList.toggle('hidden', !today);
    $$('.filter').forEach(b => b.classList.toggle('active', b.dataset.filter === state.taskFilter));

    const visible = state.tasks.filter(taskVisible); const grouped = new Map();
    visible.forEach(t => { const s=normalizeSection(t.section); if(!grouped.has(s)) grouped.set(s,[]); grouped.get(s).push(t); });
    const container=$('#taskList'); container.replaceChildren();
    for (const [section, items] of grouped) {
      const group=document.createElement('section'); group.className='task-group';
      const h=document.createElement('button'); h.type='button'; h.className='group-header'; h.dataset.taskGroup=section; h.textContent=section; group.append(h);
      if (state.expanded[state.taskTab].has(section)) { const list=document.createElement('div'); list.className='group-tasks'; items.forEach(t=>list.append(createTaskRow(t))); group.append(list); }
      container.append(group);
    }
    $('#emptyState').classList.toggle('hidden', visible.length !== 0);
  }

  function showTaskDetail(task) {
    cancelDescriptionEdit();
    cancelTitleEdit();
    state.selectedTaskId=task.id; $('#listView').classList.add('hidden'); $('#detailView').classList.remove('hidden');
    $('#detailSection').textContent=normalizeSection(task.section); $('#detailTitle').textContent=task.title; $('#detailDescription').textContent=task.description;
    const actions=$('#detailActions'); actions.replaceChildren();
    const move=document.createElement('button'); move.type='button'; move.className='detail-move'; move.dataset.taskMove=task.id; move.textContent=task.bucket==='backlog'?'→ Сегодня':'← Backlog'; actions.append(move);
    if(task.bucket==='today'){const b=document.createElement('button');b.type='button';b.className='detail-status';b.dataset.taskAdvance=task.id;b.textContent=statusLabel[task.status];actions.append(b);}
    const del=document.createElement('button');del.type='button';del.className='detail-delete';del.dataset.taskDelete=task.id;del.textContent='Удалить';actions.append(del);
  }
  function closeTaskDetail(){state.selectedTaskId=null;$('#detailView').classList.add('hidden');$('#listView').classList.remove('hidden');}
  async function moveTask(id){const t=state.tasks.find(x=>x.id===id);if(!t)return;const updated=await api(`/api/tasks/${id}/bucket`,{method:'PUT',body:JSON.stringify({bucket:t.bucket==='backlog'?'today':'backlog'})});Object.assign(t,updated,{section:normalizeSection(updated.section)});renderTasks();if(state.selectedTaskId===id)showTaskDetail(t);}
  async function advanceTask(id){const t=state.tasks.find(x=>x.id===id);if(!t)return;const r=await api(`/api/tasks/${id}/advance`,{method:'PUT',body:'{}'});if(r.deleted){state.tasks=state.tasks.filter(x=>x.id!==id);if(state.selectedTaskId===id)closeTaskDetail();}else Object.assign(t,r,{section:normalizeSection(r.section)});renderTasks();if(state.selectedTaskId===id&&state.tasks.includes(t))showTaskDetail(t);}
  async function deleteTask(id){if(!confirm('Удалить задачу?'))return;const t=state.tasks.find(x=>x.id===id);if(!t)return;await api(`/api/tasks/${id}`,{method:'DELETE'});state.tasks=state.tasks.filter(x=>x.id!==id);if(state.selectedTaskId===id)closeTaskDetail();renderTasks();showToast('Задача удалена');}

  // -------- Редактирование описания задачи --------
  function beginDescriptionEdit() {
    if (state.editingDescription || !state.selectedTaskId) return;
    const p = $('#detailDescription');
    const textarea = document.createElement('textarea');
    textarea.id = 'detailDescriptionInput'; textarea.className = 'description-input';
    textarea.value = p.textContent;
    textarea.addEventListener('keydown', e => { if (e.key === 'Escape') { e.preventDefault(); cancelDescriptionEdit(); } });
    const save = document.createElement('button');
    save.type = 'button'; save.id = 'detailDescriptionSave'; save.className = 'description-save';
    save.textContent = 'OK';
    save.addEventListener('click', () => saveDescriptionEdit().catch(err => showToast(err.message)));
    state.editingDescription = true;
    p.classList.add('hidden');
    p.closest('.description-card').append(textarea, save);
    textarea.focus();
  }
  function cancelDescriptionEdit() {
    if (!state.editingDescription) return;
    const ta = $('#detailDescriptionInput'); if (ta) ta.remove();
    const ok = $('#detailDescriptionSave'); if (ok) ok.remove();
    $('#detailDescription').classList.remove('hidden');
    state.editingDescription = false;
  }
  async function saveDescriptionEdit() {
    if (!state.editingDescription) return;
    const ta = $('#detailDescriptionInput'); const id = state.selectedTaskId;
    const t = state.tasks.find(x => x.id === id); if (!t) return;
    const value = ta.value;
    if (value === t.description) { cancelDescriptionEdit(); return; }
    const updated = await api(`/api/tasks/${id}/description`, { method: 'PUT', body: JSON.stringify({ description: value }) });
    Object.assign(t, updated, { section: normalizeSection(updated.section) });
    cancelDescriptionEdit();
    renderTasks();
    if (state.selectedTaskId === id) { $('#detailDescription').textContent = updated.description; }
    showToast('Описание сохранено');
  }

  // -------- Редактирование заголовка задачи --------
  function beginTitleEdit() {
    if (state.editingTitle || !state.selectedTaskId) return;
    const h = $('#detailTitle');
    const input = document.createElement('input');
    input.id = 'detailTitleInput'; input.className = 'description-input';
    input.value = h.textContent;
    input.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.preventDefault(); cancelTitleEdit(); }
      else if (e.key === 'Enter') { e.preventDefault(); saveTitleEdit().catch(err => showToast(err.message)); }
    });
    const save = document.createElement('button');
    save.type = 'button'; save.id = 'detailTitleSave'; save.className = 'description-save';
    save.textContent = 'OK';
    save.addEventListener('click', () => saveTitleEdit().catch(err => showToast(err.message)));
    state.editingTitle = true;
    h.classList.add('hidden');
    const wrap = h.parentElement;
    wrap.insertBefore(input, h.nextSibling);
    wrap.insertBefore(save, input.nextSibling);
    input.focus();
  }
  function cancelTitleEdit() {
    if (!state.editingTitle) return;
    const input = $('#detailTitleInput'); if (input) input.remove();
    const ok = $('#detailTitleSave'); if (ok) ok.remove();
    $('#detailTitle').classList.remove('hidden');
    state.editingTitle = false;
  }
  async function saveTitleEdit() {
    if (!state.editingTitle) return;
    const input = $('#detailTitleInput'); const id = state.selectedTaskId;
    const t = state.tasks.find(x => x.id === id); if (!t) return;
    const value = input.value.trim();
    if (!value || value === t.title) { cancelTitleEdit(); return; }
    const updated = await api(`/api/tasks/${id}/title`, { method: 'PUT', body: JSON.stringify({ title: value }) });
    Object.assign(t, updated, { section: normalizeSection(updated.section) });
    cancelTitleEdit();
    renderTasks();
    if (state.selectedTaskId === id) { $('#detailTitle').textContent = updated.title; }
    showToast('Заголовок сохранён');
  }

  // -------- Memory --------
  async function loadMemory() {
    const [dashboard,status,projects,agents]=await Promise.all([api('/api/memory/dashboard'),api('/api/memory/status'),api('/api/memory/projects'),api('/api/memory/agents')]);
    state.dashboard=dashboard; state.memoryStatus=status;
    fillSelect($('#lessonProject'), projects, 'Все проекты'); fillSelect($('#problemProject'), projects, 'Все проекты'); fillSelect($('#lessonAgent'), agents, 'Все агенты');
    renderOverview(); renderSystem(); await Promise.all([searchLessons(),searchProblems(),searchSkills()]);
  }

  function fillSelect(select, values, allLabel){select.replaceChildren();const all=document.createElement('option');all.value='';all.textContent=allLabel;select.append(all);values.forEach(v=>{const o=document.createElement('option');o.value=v;o.textContent=v;select.append(o);});}
  function setMemoryTab(tab){state.memoryTab=tab;const ids={overview:'#paneOverview',lessons:'#paneLessons',problems:'#paneProblems',skills:'#paneSkills',system:'#paneSystem'};Object.entries(ids).forEach(([k,id])=>$(id).classList.toggle('hidden',k!==tab));$$('.memory-tab').forEach(b=>b.classList.toggle('active',b.dataset.memoryTab===tab));}

  function renderOverview(){
    const m=state.dashboard.metrics; const kpis=[['Уроков',m.lessonsTotal],['Применений',m.appliedTotal],['Подтверждено',m.verifiedTotal],['Проблем',m.problemsTotal],['Скиллов',m.skillsTotal]];
    $('#kpiRow').innerHTML=kpis.map(([l,v])=>`<div class="kpi"><div class="kpi-label">${l}</div><div class="kpi-value">${v}</div></div>`).join('');
    const top=[...state.dashboard.lessons].sort((a,b)=>b.appliedCount-a.appliedCount).slice(0,5); const max=Math.max(1,...top.map(x=>x.appliedCount));
    const box=$('#topLessons'); box.replaceChildren(); top.forEach(l=>{const b=document.createElement('button');b.type='button';b.className='rank-row';b.dataset.lessonOpen=l.id;b.innerHTML=`<div class="rank-head"><span class="rank-title">${escapeHtml(l.title)}</span><span class="rank-stats">${l.appliedCount} · ${l.verifiedCount} подтверждено</span></div><div class="bar-track"><div class="bar-fill" style="width:${Math.round(l.appliedCount/max*100)}%"></div></div>`;box.append(b);});
    renderSecondary();
  }

  function setSecondary(tab){state.secondary=tab;$$('.secondary-tab').forEach(b=>b.classList.toggle('active',b.dataset.secondary===tab));renderSecondary();}
  function renderSecondary(){
    const c=$('#secondaryContent'); c.replaceChildren(); const m=state.dashboard.metrics;
    if(state.secondary==='recent'){const list=document.createElement('div');list.className='simple-list';m.recentPrepares.forEach(x=>{const d=document.createElement('div');d.className='simple-item';d.innerHTML=`<span><b>${escapeHtml(x.agent)}</b> · ${escapeHtml(projectName(x.projectId))}<br><span class="muted">${escapeHtml(x.task)}</span></span><span class="muted">${x.foundRecords} запис.</span>`;list.append(d);});c.append(list);return;}
    const src=state.secondary==='agents'?m.byAgent:m.byProject; const rows=Object.entries(src).map(([name,v])=>({name:state.secondary==='projects'?projectName(name):name,...v})).sort((a,b)=>b.applied-a.applied);const max=Math.max(1,...rows.map(x=>x.applied));const chart=document.createElement('div');chart.className='mini-chart';rows.forEach(r=>{const d=document.createElement('div');d.className='mini-row';d.innerHTML=`<div class="row-head"><span>${escapeHtml(r.name)}</span><span class="muted">${r.applied} · ✓${r.verified}</span></div><div class="bar-track"><div class="bar-fill" style="width:${Math.round(r.applied/max*100)}%"></div></div>`;chart.append(d);});c.append(chart);
  }

  async function searchLessons(){const p=new URLSearchParams();const q=$('#lessonSearch').value.trim(),project=$('#lessonProject').value,agent=$('#lessonAgent').value;if(q)p.set('q',q);if(project)p.set('project',project);if(agent)p.set('agent',agent);const lessons=await api('/api/memory/lessons?'+p.toString());const box=$('#lessonList');box.replaceChildren();lessons.forEach(l=>box.append(createLessonRow(l)));$('#lessonEmpty').classList.toggle('hidden',lessons.length>0);}
  function createLessonRow(l){const b=document.createElement('button');b.type='button';b.className='lesson-row';b.dataset.lessonOpen=l.id;b.innerHTML=`<div class="min-w-0"><div class="lesson-title">${escapeHtml(l.title)}</div><div class="lesson-mobile-meta">${escapeHtml(l.project)} · ${escapeHtml(l.agent)} · ${escapeHtml(l.scope)}</div></div><div class="lesson-meta"><div>${escapeHtml(l.project)}</div><div>${escapeHtml(l.agent)} · ${escapeHtml(l.scope)}</div></div><div class="lesson-stats">${l.appliedCount} примен.<br>✓ ${l.verifiedCount} подтверждено</div>`;return b;}

  async function searchProblems(){const p=new URLSearchParams();const q=$('#problemSearch').value.trim(),project=$('#problemProject').value;if(q)p.set('q',q);if(project)p.set('project',project);const items=await api('/api/memory/problems?'+p.toString());const box=$('#problemList');box.replaceChildren();items.forEach(p=>{const b=document.createElement('button');b.type='button';b.className='problem-row';b.innerHTML=`<div class="row-main"><div class="row-title">${escapeHtml(p.title)}</div><div class="row-sub">${escapeHtml(p.summary)}</div><div class="row-extra">${escapeHtml(p.project)} · ${p.solutionCount} решений</div></div><div class="row-side">${p.appliedCount} примен.<br>✓ ${p.verifiedCount}</div>`;box.append(b);});$('#problemEmpty').classList.toggle('hidden',items.length>0);}

  async function searchSkills(){const p=new URLSearchParams();const q=$('#skillSearch').value.trim();if(q)p.set('q',q);const items=await api('/api/memory/skills?'+p.toString());const box=$('#skillList');box.replaceChildren();items.forEach(s=>{const d=document.createElement('div');d.className='skill-row'+(s.status==='obsolete'?' muted':'');d.innerHTML=`<div class="row-main"><div class="row-title">${escapeHtml(s.name)}</div><div class="row-sub">${escapeHtml(s.description)}</div></div><div class="row-side">${escapeHtml(s.project)}<br>${escapeHtml(s.version)} · ${escapeHtml(s.status)}</div>`;box.append(d);});$('#skillEmpty').classList.toggle('hidden',items.length>0);}

  function renderSystem(){const s=state.memoryStatus;const cards=[['База',s.database],['Hindsight',s.hindsight],['Worker',s.worker],['Очередь',{status:`${s.queue.queued} queued · ${s.queue.failed} failed`,requiresAttention:s.queue.failed>0}]];$('#systemCards').innerHTML=cards.map(([name,v])=>`<div class="system-card"><div class="system-title"><span class="status-dot ${v.requiresAttention?'work':'done'}"></span>${name}</div><div class="system-sub">${escapeHtml(v.status)}</div></div>`).join('');$('#systemJobs').innerHTML=s.jobs.map(j=>`<div class="simple-item"><span>${escapeHtml(j.id)}</span><span class="muted">${escapeHtml(j.status)}</span></div>`).join('');$('#systemComputers').innerHTML=s.computers.map(c=>`<div class="simple-item"><span>${escapeHtml(c.name)}</span><span class="muted">${escapeHtml(c.status)}</span></div>`).join('');}

  function openLesson(id){const l=state.dashboard.lessons.find(x=>String(x.id)===String(id));if(!l)return;state.selectedLesson=id;$('#memoryMain').classList.add('hidden');$('#lessonDetail').classList.remove('hidden');$('#lessonDetailProject').textContent=l.project;$('#lessonDetailScope').textContent=`${l.scope} · ${l.status}`;$('#lessonDetailTitle').textContent=l.title;$('#lessonDetailMeta').textContent=`${l.agent} · ${l.appliedCount} применений · ${l.verifiedCount} подтверждено`;$('#lessonProblem').textContent=l.problem;$('#lessonConditions').textContent=l.conditions;$('#lessonCause').textContent=l.cause;$('#lessonMethod').textContent=l.workingMethod;$('#lessonEvidence').textContent=`${l.evidence} ${l.verification}`;}
  function closeLesson(){state.selectedLesson=null;$('#lessonDetail').classList.add('hidden');$('#memoryMain').classList.remove('hidden');}
  function projectName(id){if(id==='lost-cyber-hamster-2025')return'Lost Cyber Hamster';if(id==='agent-memory-system')return'Общая память агентов';if(id==='*')return'Общий опыт';return id;}
  function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[ch]));}
  function debounce(fn,delay=220){let timer;return(...args)=>{clearTimeout(timer);timer=setTimeout(()=>fn(...args),delay);};}

  // -------- Кнопки «Свернуть все» / «Развернуть все»: шевроны вместо текста --------
  (function () {
    [['collapseAll', 'Свернуть все', '› ‹'], ['expandAll', 'Развернуть все', '‹ ›']].forEach(([id, label, glyphs]) => {
      const b = $(`#${id}`); if (!b) return;
      if (!b.title) b.title = label;
      if (!b.getAttribute('aria-label')) b.setAttribute('aria-label', label);
      b.replaceChildren(glyphs);
    });
  })();

  // -------- Events --------
  $$('[data-main]').forEach(b=>b.addEventListener('click',()=>setMain(b.dataset.main)));
  $('#backlogTab').addEventListener('click',()=>{state.taskTab='backlog';state.taskFilter='all';closeTaskDetail();renderTasks();});
  $('#todayTab').addEventListener('click',()=>{state.taskTab='today';state.taskFilter='all';closeTaskDetail();renderTasks();});
  $$('.filter').forEach(b=>b.addEventListener('click',()=>{state.taskFilter=b.dataset.filter;renderTasks();}));
  $('#collapseAll').addEventListener('click',()=>{state.expanded[state.taskTab].clear();renderTasks();});
  $('#expandAll').addEventListener('click',()=>{state.tasks.filter(taskVisible).forEach(t=>state.expanded[state.taskTab].add(normalizeSection(t.section)));renderTasks();});
  $('#backButton').addEventListener('click', closeTaskDetail);
    $('#detailDescription').addEventListener('click', beginDescriptionEdit);
    $('#detailTitle').addEventListener('click', beginTitleEdit);
  $('#addForm').addEventListener('submit',async e=>{e.preventDefault();const btn=$('#addForm button[type="submit"]');const text=$('#taskInput').value.trim();if(!text)return;const orig=btn.textContent;const dots=['.','..','...'];let i=0;btn.disabled=true;btn.classList.add('sending');btn.textContent=dots[0];const timer=setInterval(()=>{i=(i+1)%dots.length;btn.textContent=dots[i];},375);try{const item=await api('/api/tasks',{method:'POST',body:JSON.stringify({text})});state.tasks.unshift({...item,section:normalizeSection(item.section)});$('#taskInput').value='';renderTasks();}catch(err){showToast(err.message);}finally{clearInterval(timer);btn.disabled=false;btn.classList.remove('sending');btn.textContent=orig;}});

  $$('.memory-tab').forEach(b=>b.addEventListener('click',()=>setMemoryTab(b.dataset.memoryTab)));
  $$('.secondary-tab').forEach(b=>b.addEventListener('click',()=>setSecondary(b.dataset.secondary)));
  $('#lessonSearch').addEventListener('input',debounce(()=>searchLessons().catch(e=>showToast(e.message))));
  $('#lessonProject').addEventListener('change',()=>searchLessons().catch(e=>showToast(e.message)));
  $('#lessonAgent').addEventListener('change',()=>searchLessons().catch(e=>showToast(e.message)));
  $('#problemSearch').addEventListener('input',debounce(()=>searchProblems().catch(e=>showToast(e.message))));
  $('#problemProject').addEventListener('change',()=>searchProblems().catch(e=>showToast(e.message)));
  $('#skillSearch').addEventListener('input',debounce(()=>searchSkills().catch(e=>showToast(e.message))));
  $('#lessonBack').addEventListener('click',closeLesson);

  document.addEventListener('click',async e=>{
    try{
      const g=e.target.closest('[data-task-group]');if(g){const set=state.expanded[state.taskTab];set.has(g.dataset.taskGroup)?set.delete(g.dataset.taskGroup):set.add(g.dataset.taskGroup);renderTasks();return;}
      const o=e.target.closest('[data-task-open]');if(o){const t=state.tasks.find(x=>x.id===o.dataset.taskOpen);if(t)showTaskDetail(t);return;}
      const m=e.target.closest('[data-task-move]');if(m){await moveTask(m.dataset.taskMove);return;}
      const a=e.target.closest('[data-task-advance]');if(a){await advanceTask(a.dataset.taskAdvance);return;}
      const d=e.target.closest('[data-task-delete]');if(d){await deleteTask(d.dataset.taskDelete);return;}
      const l=e.target.closest('[data-lesson-open]');if(l){openLesson(l.dataset.lessonOpen);return;}
    }catch(err){showToast(err.message);}
  });

  async function init(){try{await Promise.all([loadTasks(),loadMemory()]);setMain('tasks');setMemoryTab('overview');setSecondary('agents');}catch(err){showToast(err.message);}}
  init();
})();
