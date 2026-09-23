(() => {
  const state = {
    main: 'tasks', tasks: [], taskTab: 'backlog', taskFilter: 'all', selectedTaskId: null, renamingSection: null,
    expanded: { backlog: new Set(), today: new Set() },
    dashboard: null, memoryStatus: null, memoryTab: 'overview', secondary: 'agents', selectedLesson: null, selectedProblem: null, selectedSkill: null, problems: [], skills: [],
    lessonSort: 'added', recentTab: 'reads', knowledge: [], knowledgeExpanded: new Set(), knowledgeDocument: null, knowledgeEditing: false
  };

  const $ = s => document.querySelector(s);
  const $$ = s => [...document.querySelectorAll(s)];
  const toast = $('#toast');
  const statusLabel = { new: 'В работу', in_progress: 'Завершить', completed: 'Стикер' };

  function showToast(message, duration = 1800) {
    toast.textContent = message; toast.classList.remove('hidden');
    clearTimeout(showToast.timer); showToast.timer = setTimeout(() => toast.classList.add('hidden'), duration);
  }

  const chatModelKey = 'personalDashboardChatModel';
  const chatModelNames = { deepseek: 'DeepSeek V4 Flash 0731', gemma: 'Gemma 4 E4B' };
  function selectedChatModel() {
    return localStorage.getItem(chatModelKey) === 'gemma' ? 'gemma' : 'deepseek';
  }
  function renderChatModelToggle() {
    const model = selectedChatModel();
    const name = chatModelNames[model];
    ['#chatModelToggleTasks', '#chatModelToggleKnowledge'].forEach(selector => {
      const button = $(selector);
      button.textContent = model === 'deepseek' ? 'D' : 'G';
      button.setAttribute('aria-label', `Активная модель ${name}. Переключить модель`);
      button.title = `Активная модель ${name}. Нажмите, чтобы переключить`;
      button.setAttribute('aria-pressed', String(model === 'gemma'));
    });
  }
  function toggleChatModel() {
    const model = selectedChatModel() === 'deepseek' ? 'gemma' : 'deepseek';
    localStorage.setItem(chatModelKey, model);
    renderChatModelToggle();
    showToast(`Активирована модель ${chatModelNames[model]}`, 2500);
  }
  renderChatModelToggle();
  $('#chatModelToggleTasks').addEventListener('click', toggleChatModel);
  $('#chatModelToggleKnowledge').addEventListener('click', toggleChatModel);

  async function api(url, options = {}) {
    const response = await fetch(url, { ...options, headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } });
    if (!response.ok) { let m='Ошибка запроса.'; try { m=(await response.json()).message||m; } catch {} throw new Error(m); }
    return response.status === 204 ? null : response.json();
  }

  const normalizeSection = s => String(s || '').trim() || 'Общее';
  const statusDotClass = s => s === 'in_progress' ? 'work' : s === 'completed' ? 'done' : 'new';

  // -------- Черновик новой задачи --------
  // Пока пользователь не нажмёт «ОК, добавить», результат агента существует
  // только в браузере: отмена не создаёт лишнюю задачу.
  let taskDraftSession = null;

  function renderTaskDraft() {
    if (!taskDraftSession) return;
    const { draft, iteration } = taskDraftSession;
    $('#taskDraftIteration').textContent = `Вариант ${iteration}`;
    $('#taskDraftSection').textContent = normalizeSection(draft.section);
    $('#taskDraftResultTitle').textContent = draft.title;
    $('#taskDraftDescription').textContent = draft.description;
  }

  function setTaskDraftBusy(busy, message = '') {
    const modal = $('#taskDraftModal');
    modal.setAttribute('aria-busy', String(busy));
    $('#taskDraftCorrection').disabled = busy;
    $('#taskDraftCancel').disabled = busy;
    $('#taskDraftRevise').disabled = busy;
    $('#taskDraftConfirm').disabled = busy;
    $('#taskDraftStatus').textContent = message;
  }

  function openTaskDraft(draft, editingTaskId = null) {
    taskDraftSession = { draft, iteration: 1, editingTaskId };
    $('#taskDraftCorrection').value = '';
    setTaskDraftBusy(false);
    const editing = editingTaskId != null;
    $('#taskDraftHint').textContent = editing
      ? 'Изменения применятся к задаче после подтверждения. Можно попросить агента поправить ещё раз.'
      : 'Задача ещё не сохранена. Подтвердите результат или попросите агента его изменить.';
    $('#taskDraftConfirm').textContent = editing ? 'ОК, применить' : 'ОК, добавить';
    renderTaskDraft();
    $('#taskDraftModal').showModal();
    $('#taskDraftCorrection').focus();
  }

  function closeTaskDraft() {
    if ($('#taskDraftModal').open) $('#taskDraftModal').close();
    taskDraftSession = null;
  }

  async function reviseTaskDraft() {
    if (!taskDraftSession) return;
    const correction = $('#taskDraftCorrection').value.trim();
    if (!correction) {
      $('#taskDraftCorrection').focus();
      $('#taskDraftStatus').textContent = 'Опишите правку, чтобы отправить её агенту.';
      return;
    }
    setTaskDraftBusy(true, 'Агент обновляет черновик…');
    try {
      const draft = await api('/api/tasks/draft/revise', { method: 'POST', body: JSON.stringify({ draft: taskDraftSession.draft, correction }) });
      taskDraftSession = { draft, iteration: taskDraftSession.iteration + 1 };
      $('#taskDraftCorrection').value = '';
      renderTaskDraft();
      setTaskDraftBusy(false, 'Черновик обновлён. Проверьте результат.');
      $('#taskDraftCorrection').focus();
    } catch (err) {
      setTaskDraftBusy(false, err.message);
      showToast(err.message);
    }
  }

  async function confirmTaskDraft() {
    if (!taskDraftSession) return;
    const editingTaskId = taskDraftSession.editingTaskId;
    setTaskDraftBusy(true, editingTaskId != null ? 'Применяем правку…' : 'Добавляем задачу…');
    try {
      if (editingTaskId != null) {
        const updated = await api(`/api/tasks/${editingTaskId}/edit`, { method: 'PUT', body: JSON.stringify({ draft: taskDraftSession.draft }) });
        const current = state.tasks.find(x => x.id === editingTaskId);
        if (current) Object.assign(current, updated, { section: normalizeSection(updated.section) });
        closeTaskDraft();
        renderTasks();
        if (state.selectedTaskId === editingTaskId) showTaskDetail(current ?? updated);
        showToast('Задача обновлена');
        return;
      }
      const item = await api('/api/tasks/confirm', { method: 'POST', body: JSON.stringify({ draft: taskDraftSession.draft }) });
      state.tasks.unshift({ ...item, section: normalizeSection(item.section) });
      $('#taskInput').value = '';
      closeTaskDraft();
      renderTasks();
      $('#taskInput').focus();
      showToast('Задача добавлена');
    } catch (err) {
      setTaskDraftBusy(false, err.message);
      showToast(err.message);
    }
  }

  // -------- Browser navigation --------
  // Все экраны получают hash-маршрут: браузерная «Назад» возвращает предыдущий
  // экран SPA, а не перезагружает страницу или уходит к внешнему адресу.
  const routeStateKey = 'personalDashboardRoute';
  const taskTabs = new Set(['backlog', 'today']);
  const memoryTabs = new Set(['overview', 'lessons', 'problems', 'skills', 'system']);

  function parseRoute() {
    const raw = location.hash.replace(/^#/, '');
    const [path, query = ''] = raw.split('?');
    const parts = path.split('/').filter(Boolean).map(decodeURIComponent);
    const params = new URLSearchParams(query);
    if (parts[0] === 'memory') {
      const memoryTab = memoryTabs.has(parts[1]) ? parts[1] : 'overview';
      return { main: 'memory', memoryTab, lessonId: params.get('lesson'), problemId: params.get('problem'), skillId: params.get('skill') };
    }
    if (parts[0] === 'knowledge' && parts[1] === 'chat') return { main: 'knowledge', chat: true, sessionId: params.get('session') };
    if (parts[0] === 'knowledge') return { main: 'knowledge' };

    if (parts[0] === 'tasks' && parts[1] === 'chat') return { main: 'tasks', chat: true, sessionId: params.get('session') };

    const taskTab = taskTabs.has(parts[1]) ? parts[1] : 'backlog';
    const filter = taskTab === 'today' && ['new', 'in_progress', 'completed'].includes(params.get('filter'))
      ? params.get('filter')
      : 'all';
    return { main: 'tasks', taskTab, filter, taskId: params.get('task') };
  }

  function routeHash(route) {
    const params = new URLSearchParams();
    if (route.main === 'memory') {
      if (route.lessonId) params.set('lesson', route.lessonId);
      if (route.problemId) params.set('problem', route.problemId);
      if (route.skillId) params.set('skill', route.skillId);
      return `#/memory/${route.memoryTab || 'overview'}${params.size ? `?${params}` : ''}`;
    }
    if (route.chat) return `#/${route.main}/chat${route.sessionId ? `?session=${encodeURIComponent(route.sessionId)}` : ''}`;
    if (route.main === 'knowledge') return '#/knowledge';
    if (route.filter && route.filter !== 'all') params.set('filter', route.filter);
    if (route.taskId) params.set('task', route.taskId);
    return `#/tasks/${route.taskTab || 'backlog'}${params.size ? `?${params}` : ''}`;
  }

  function routeEntry(route, entry) { return { [routeStateKey]: true, entry, route }; }

  function navigate(route, { replace = false } = {}) {
    const hash = routeHash(route);
    const entry = replace ? Boolean(history.state?.entry) : false;
    history[replace ? 'replaceState' : 'pushState'](routeEntry(route, entry), '', hash);
    applyRoute(route);
  }

  function goBack(fallbackRoute) {
    if (history.state?.[routeStateKey] && !history.state.entry) history.back();
    else navigate(fallbackRoute, { replace: true });
  }

  function applyRoute(route) {
    $('#chatView').classList.toggle('hidden', !route.chat);
    if (route.chat) {
      setMain(route.main);
      $('#tasksSection').classList.add('hidden');
      $('#memorySection').classList.add('hidden');
      $('#knowledgeSection').classList.add('hidden');
      closeTaskDetail(); $('#listView').classList.add('hidden');
      syncMobileChatViewport();
      loadChat(route.main, route.sessionId);
      return;
    }
    if (route.main === 'knowledge') { setMain('knowledge'); closeKnowledgeDocument().catch(e=>showToast(e.message)); return; }
    if (route.main === 'memory') {
      setMain('memory');
      setMemoryTab(route.memoryTab);
      closeLesson(); closeProblem(); closeSkill();
      if (route.lessonId) openLesson(route.lessonId);
      else if (route.problemId) openProblem(route.problemId);
      else if (route.skillId) openSkill(route.skillId);
      return;
    }

    state.taskTab = route.taskTab;
    state.taskFilter = route.filter;
    setMain('tasks');
    $('#chatView').classList.add('hidden');
    syncMobileChatViewport();
    const task = route.taskId && state.tasks.find(item => String(item.id) === String(route.taskId));
    if (task) {
      state.taskTab = task.bucket;
      state.taskFilter = 'all';
      showTaskDetail(task);
    } else {
      closeTaskDetail();
      renderTasks();
    }
  }

  let chatScope = 'tasks';
  async function loadChat(scope, sessionId) {
    chatScope = scope || 'tasks';
    if (!sessionId) { renderChat({ messages: [] }); requestAnimationFrame(() => $('#chatInput').focus()); return; }
    const data = await api(`/api/${chatScope}/chat/sessions/${encodeURIComponent(sessionId)}`);
    renderChat(data);
    $('#chatInput').focus();
  }
  function renderChat(data) {
    const box = $('#chatMessages'); box.textContent = '';
    (data.messages || []).forEach(message => { const bubble = document.createElement('div'); bubble.className = `chat-bubble ${message.role === 'user' ? 'chat-user' : 'chat-agent'}`; bubble.textContent = message.text; box.append(bubble); });
    box.scrollTop = box.scrollHeight;
  }

  function appendChatBubble(role, text = '') {
    const bubble = document.createElement('div');
    bubble.className = `chat-bubble ${role === 'user' ? 'chat-user' : 'chat-agent'}`;
    bubble.textContent = text;
    $('#chatMessages').append(bubble);
    $('#chatMessages').scrollTop = $('#chatMessages').scrollHeight;
    return bubble;
  }

  function syncMobileChatViewport() {
    const viewport = window.visualViewport;
    const top = viewport?.offsetTop || 0;
    const height = viewport?.height || window.innerHeight;
    const keyboardInset = Math.max(0, window.innerHeight - top - height);
    const chatOpen = !$('#chatView').classList.contains('hidden');
    const inputFocused = document.activeElement === $('#chatInput');
    const nav = $('.bottom-nav');
    document.documentElement.style.setProperty('--chat-viewport-top', `${top}px`);
    if (nav) nav.style.bottom = chatOpen && inputFocused ? `${keyboardInset}px` : '';
    document.documentElement.style.setProperty('--chat-nav-bottom', `${(nav?.offsetHeight || 0) + 8 + (chatOpen && inputFocused ? keyboardInset : 0)}px`);
    if (chatOpen && inputFocused) requestAnimationFrame(() => {
      const messages = $('#chatMessages');
      messages.scrollTop = messages.scrollHeight;
    });
  }

  window.visualViewport?.addEventListener('resize', syncMobileChatViewport);
  window.visualViewport?.addEventListener('scroll', syncMobileChatViewport);
  window.addEventListener('resize', syncMobileChatViewport);
  $('#chatInput').addEventListener('focus', syncMobileChatViewport);
  $('#chatInput').addEventListener('blur', () => setTimeout(syncMobileChatViewport, 80));
  let chatSending = false;
  async function sendChatMessage(text) {
    if (chatSending) return;
    chatSending = true;
    const model = selectedChatModel();
    const scope = chatScope;
    const route = parseRoute();
    appendChatBubble('user', text);
    const pendingBubble = appendChatBubble('agent');
    pendingBubble.classList.add('chat-pending');
    pendingBubble.setAttribute('aria-label', 'Ответ готовится');
    let dotCount = 1;
    pendingBubble.textContent = '.';
    const pendingTimer = setInterval(() => { dotCount = dotCount % 3 + 1; pendingBubble.textContent = '.'.repeat(dotCount); }, 350);
    try {
      const data = route.sessionId
        ? await api(`/api/${scope}/chat/sessions/${encodeURIComponent(route.sessionId)}/messages`, { method: 'POST', body: JSON.stringify({ text, model }) })
        : await api(`/api/${scope}/chat/sessions`, { method: 'POST', body: JSON.stringify({ text, model }) });
      const sessionId = data.session.id;
      const messages = data.session.messages || [];
      const answer = [...messages].reverse().find(message => message.role !== 'user');
      clearInterval(pendingTimer);
      pendingBubble.classList.remove('chat-pending');
      pendingBubble.removeAttribute('aria-label');
      pendingBubble.textContent = answer?.text || 'Ответ не получен.';
      if (data.reply?.changedData) {
        try { await (scope === 'tasks' ? loadTasks() : loadKnowledge()); }
        catch { showToast('Изменение сохранено, но список не обновился. Обновите страницу.'); }
      }
      if (!route.sessionId) {
        const sessionRoute = { main: scope, chat: true, sessionId };
        history.replaceState(routeEntry(sessionRoute, Boolean(history.state?.entry)), '', routeHash(sessionRoute));
      }
    } catch (error) {
      clearInterval(pendingTimer);
      pendingBubble.classList.remove('chat-pending');
      pendingBubble.removeAttribute('aria-label');
      pendingBubble.textContent = `Ошибка: ${error.message}`;
      throw error;
    } finally {
      chatSending = false;
    }
  }

  function applyCurrentRoute() {
    const route = parseRoute();
    const current = history.state;
    history.replaceState(routeEntry(route, current?.[routeStateKey] ? Boolean(current.entry) : true), '', routeHash(route));
    applyRoute(route);
  }

  // -------- Main navigation --------
  function setMain(value) {
    state.main = value;
    $('#tasksSection').classList.toggle('hidden', value !== 'tasks');
    $('#memorySection').classList.toggle('hidden', value !== 'memory');
    $('#knowledgeSection').classList.toggle('hidden', value !== 'knowledge');
    $$('[data-main]').forEach(b => b.classList.toggle('active', b.dataset.main === value));
  }

  const bottomNavOrderKey = 'personalDashboardBottomNavOrder';
  let bottomNavGesture = null;
  let bottomNavAnimationFrame = 0;

  function restoreBottomNavOrder() {
    const nav = $('.bottom-nav');
    if (!nav) return;
    let saved = [];
    try { saved = JSON.parse(localStorage.getItem(bottomNavOrderKey) || '[]'); } catch {}
    const buttons = [...nav.querySelectorAll('.bottom-item')];
    const byMain = new Map(buttons.map(button => [button.dataset.main, button]));
    const order = [...saved.filter(main => byMain.has(main)), ...buttons.map(button => button.dataset.main).filter(main => !saved.includes(main))];
    order.forEach(main => nav.append(byMain.get(main)));
  }

  function saveBottomNavOrder() {
    const nav = $('.bottom-nav');
    if (nav) localStorage.setItem(bottomNavOrderKey, JSON.stringify([...nav.querySelectorAll('.bottom-item')].map(button => button.dataset.main)));
  }

  function updateBottomNavIndicators() {
    const nav = $('.bottom-nav');
    if (!nav) return;
    nav.classList.toggle('has-left', nav.scrollLeft > 2);
    nav.classList.toggle('has-right', nav.scrollLeft + nav.clientWidth < nav.scrollWidth - 2);
  }

  function snapBottomNav(velocityX = 0) {
    const nav = $('.bottom-nav');
    if (!nav || nav.scrollWidth <= nav.clientWidth) return;
    const buttons = [...nav.querySelectorAll('.bottom-item')];
    if (buttons.length < 2) return;
    const step = buttons[1].getBoundingClientRect().left - buttons[0].getBoundingClientRect().left;
    if (!step) return;
    const projected = nav.scrollLeft - velocityX * 180;
    const target = Math.max(0, Math.min(nav.scrollWidth - nav.clientWidth, Math.round(projected / step) * step));
    cancelAnimationFrame(bottomNavAnimationFrame);
    const start = nav.scrollLeft;
    const distance = target - start;
    if (matchMedia('(prefers-reduced-motion: reduce)').matches || Math.abs(distance) < 1) {
      nav.scrollLeft = target;
      updateBottomNavIndicators();
      return;
    }
    const startedAt = performance.now();
    const duration = Math.min(360, Math.max(220, Math.abs(distance) * 0.55));
    const animate = now => {
      const progress = Math.min(1, (now - startedAt) / duration);
      const eased = 1 - (1 - progress) ** 3;
      nav.scrollLeft = start + distance * eased;
      updateBottomNavIndicators();
      if (progress < 1) bottomNavAnimationFrame = requestAnimationFrame(animate);
      else bottomNavAnimationFrame = 0;
    };
    bottomNavAnimationFrame = requestAnimationFrame(animate);
  }

  function initBottomNavGestures() {
    const nav = $('.bottom-nav');
    if (!nav) return;
    restoreBottomNavOrder();
    nav.addEventListener('scroll', updateBottomNavIndicators, { passive: true });
    nav.addEventListener('pointerdown', event => {
      if (event.button !== 0 || bottomNavGesture) return;
      const button = event.target.closest('.bottom-item');
      event.preventDefault();
      cancelAnimationFrame(bottomNavAnimationFrame);
      bottomNavAnimationFrame = 0;
      nav.dataset.suppressClick = 'false';
      const gesture = bottomNavGesture = {
        button, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY,
        startScrollLeft: nav.scrollLeft, dragging: false, moved: false, armed: false,
        lastX: event.clientX, lastTime: event.timeStamp, velocityX: 0,
        ghost: null, dropTarget: null, offsetX: 0, offsetY: 0,
        timer: button ? setTimeout(() => { if (bottomNavGesture === gesture) gesture.armed = true; }, 420) : null
      };
      nav.setPointerCapture?.(event.pointerId);
    });
    nav.addEventListener('pointermove', event => {
      const gesture = bottomNavGesture;
      if (!gesture || gesture.pointerId !== event.pointerId) return;
      const dx = event.clientX - gesture.startX;
      const dy = event.clientY - gesture.startY;
      const elapsed = Math.max(1, event.timeStamp - gesture.lastTime);
      const sampleVelocity = (event.clientX - gesture.lastX) / elapsed;
      gesture.velocityX = gesture.velocityX * 0.65 + sampleVelocity * 0.35;
      gesture.lastX = event.clientX;
      gesture.lastTime = event.timeStamp;
      if (!gesture.dragging && (Math.abs(dx) > 10 || Math.abs(dy) > 10)) {
        gesture.moved = true;
        if (gesture.armed && gesture.button) {
          gesture.dragging = true;
          clearTimeout(gesture.timer);
          const rect = gesture.button.getBoundingClientRect();
          gesture.offsetX = gesture.startX - rect.left;
          gesture.offsetY = gesture.startY - rect.top;
          gesture.button.classList.add('bottom-item-dragging');
          gesture.ghost = gesture.button.cloneNode(true);
          gesture.ghost.classList.add('bottom-item-drag-ghost');
          gesture.ghost.setAttribute('aria-hidden', 'true');
          gesture.ghost.inert = true;
          gesture.ghost.style.width = `${rect.width}px`;
          document.body.append(gesture.ghost);
        }
      }
      if (!gesture.dragging) {
        if (gesture.moved) {
          clearTimeout(gesture.timer);
          nav.scrollLeft = gesture.startScrollLeft - dx;
          updateBottomNavIndicators();
        }
        return;
      }
      event.preventDefault();
      gesture.ghost.style.left = `${event.clientX - gesture.offsetX}px`;
      gesture.ghost.style.top = `${event.clientY - gesture.offsetY}px`;
      const target = document.elementFromPoint(event.clientX, event.clientY)?.closest('.bottom-item');
      if (!target || target === gesture.button || target.parentElement !== nav) {
        gesture.dropTarget?.classList.remove('bottom-item-drop-target');
        gesture.dropTarget = null;
        return;
      }
      const rect = target.getBoundingClientRect();
      const before = event.clientX < rect.left + rect.width / 2;
      if (gesture.dropTarget !== target) {
        gesture.dropTarget?.classList.remove('bottom-item-drop-target');
        gesture.dropTarget = target;
        target.classList.add('bottom-item-drop-target');
      }
      nav.insertBefore(gesture.button, before ? target : target.nextSibling);
    });
    const finish = event => {
      const gesture = bottomNavGesture;
      if (!gesture || gesture.pointerId !== event.pointerId) return;
      clearTimeout(gesture.timer);
      if (gesture.dragging) {
        gesture.button.classList.remove('bottom-item-dragging');
        gesture.ghost?.remove();
        gesture.dropTarget?.classList.remove('bottom-item-drop-target');
        saveBottomNavOrder();
      }
      if (gesture.moved || gesture.dragging || gesture.armed) nav.dataset.suppressClick = 'true';
      if (gesture.moved && !gesture.dragging) snapBottomNav(gesture.velocityX);
      bottomNavGesture = null;
      updateBottomNavIndicators();
    };
    nav.addEventListener('pointerup', finish);
    nav.addEventListener('pointercancel', finish);
    nav.addEventListener('click', event => {
      if (nav.dataset.suppressClick !== 'true') return;
      delete nav.dataset.suppressClick;
      event.preventDefault();
      event.stopImmediatePropagation();
    }, true);
    requestAnimationFrame(updateBottomNavIndicators);
  }

  // -------- База знаний --------
  async function loadKnowledge(){ state.knowledge=await api('/api/knowledge/tree'); renderKnowledgeTree(); }
  function knowledgeFlat(nodes,out=[]){nodes.forEach(n=>{out.push(n);if(n.children)knowledgeFlat(n.children,out);});return out;}
  const collapseAllIconPath = 'M4 6 10 2 16 6 M4 14 10 18 16 14';
  const expandAllIconPath = 'M4 2 10 6 16 2 M4 18 10 14 16 18';
  function setAllSectionsToggle(button, expanded, collapseLabel, expandLabel) {
    if (!button) return;
    const label = expanded ? collapseLabel : expandLabel;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 20 20');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', expanded ? collapseAllIconPath : expandAllIconPath);
    svg.append(path);
    const text = document.createElement('span');
    text.className = 'all-sections-label';
    text.textContent = label;
    button.replaceChildren(svg, text);
    button.setAttribute('aria-label', label);
    button.setAttribute('aria-expanded', String(expanded));
    button.title = label;
  }

  function createLeafDragIcon() {
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
    svg.classList.add('drag-leaf-icon');
    svg.setAttribute('viewBox','0 0 24 24');
    svg.setAttribute('fill','none');
    svg.setAttribute('stroke','currentColor');
    svg.setAttribute('stroke-width','2.2');
    svg.setAttribute('aria-hidden','true');
    const circle=document.createElementNS('http://www.w3.org/2000/svg','circle');
    circle.setAttribute('cx','12');circle.setAttribute('cy','12');circle.setAttribute('r','7');
    svg.append(circle);
    return svg;
  }
  function renderKnowledgeTree(){const box=$('#knowledgeTree');box.replaceChildren();const render=(nodes,parent,depth=0)=>nodes.forEach(n=>{const row=document.createElement('div');row.className='knowledge-row';row.dataset.knowledgeId=n.id;row.dataset.knowledgeParent=parent||'';row.style.setProperty('--knowledge-depth',depth);const handle=document.createElement('button');handle.className='knowledge-drag-handle';handle.type='button';handle.setAttribute('aria-label','Перетащить');if(n.kind==='document') handle.append(createLeafDragIcon()); else handle.textContent='☰';row.append(handle);const title=document.createElement('button');title.type='button';title.className='knowledge-node-title '+n.kind;title.textContent=n.title;row.append(title);if(n.kind==='section'){const actions=document.createElement('span');actions.className='knowledge-inline-actions';const addDoc=document.createElement('button');addDoc.type='button';addDoc.textContent='+ MD';addDoc.dataset.knowledgeAddDoc=n.id;const addSection=document.createElement('button');addSection.type='button';addSection.textContent='+ раздел';addSection.dataset.knowledgeAddSection=n.id;actions.append(addDoc,addSection);row.append(actions);}box.append(row);if(n.kind==='section'&&state.knowledgeExpanded.has(n.id))render(n.children||[],n.id,depth+1);});render(state.knowledge,null);const root=document.createElement('div');root.className='knowledge-root-drop';root.dataset.knowledgeRoot='';root.textContent='Перетащите сюда, чтобы вернуть в корень';box.append(root);const add=document.createElement('div');add.className='knowledge-create-actions';add.innerHTML='<button type="button" data-knowledge-add-section="">+ Раздел</button><button type="button" data-knowledge-add-doc="">+ Документ MD</button>';box.append(add);const sections=knowledgeFlat(state.knowledge).filter(n=>n.kind==='section');const expanded=sections.length>0&&sections.every(n=>state.knowledgeExpanded.has(n.id));setAllSectionsToggle($('#knowledgeToggleAll'),expanded,'Свернуть всё','Развернуть всё');}
  function knowledgeNode(id){return knowledgeFlat(state.knowledge).find(n=>String(n.id)===String(id));}
  function requestKnowledgeName(heading,value='',action='Создать'){
    const dialog=$('#knowledgeNameModal'),input=$('#knowledgeNameInput');
    $('#knowledgeNameTitle').textContent=heading;$('#knowledgeNameSubmit').textContent=action;
    input.value=value;dialog.returnValue='';dialog.showModal();input.focus();if(value)input.select();
    return new Promise(resolve=>{dialog.addEventListener('close',()=>resolve(dialog.returnValue==='submit'?input.value.trim():null),{once:true});});
  }
  function requestKnowledgeDelete(title){
    const dialog=$('#knowledgeDeleteModal');
    $('#knowledgeDeleteMessage').textContent=`Удалить «${title}» вместе со всем содержимым?`;
    dialog.returnValue='';dialog.showModal();
    return new Promise(resolve=>{dialog.addEventListener('close',()=>resolve(dialog.returnValue==='delete'),{once:true});});
  }
  async function createKnowledge(kind,parentId){await api(`/api/knowledge/${kind==='section'?'sections':'documents'}`,{method:'POST',body:JSON.stringify({title:null,parentId:parentId||null})});if(parentId)state.knowledgeExpanded.add(parentId);await loadKnowledge();showToast(kind==='section'?'Раздел создан':'Документ создан');}
  function showKnowledgeDocument(doc){state.knowledgeDocument=doc;state.knowledgeEditing=false;$('#knowledgeTree').classList.add('hidden');$('#knowledgeDocument').classList.remove('hidden');$('#knowledgeDocumentTitle').textContent=doc.title;$('#knowledgeEditor').value=doc.content||'';renderKnowledgeMode();}
  function renderKnowledgeMode(){const edit=state.knowledgeEditing;$('#knowledgeModeToggle').textContent=edit?'Просмотр':'Редактирование';$('#knowledgePreview').classList.toggle('hidden',edit);$('#knowledgeEditor').classList.toggle('hidden',!edit);if(!edit)$('#knowledgePreview').innerHTML=renderMarkdown(state.knowledgeDocument?.content||'');}
  function renderMarkdown(value){const lines=escapeHtml(value).split('\n'),out=[];let code=false,buf=[],list=null;const closeList=()=>{if(list){out.push(`</${list}>`);list=null;}};for(const line of lines){if(line.startsWith('```')){closeList();if(code){out.push(`<pre><code>${buf.join('\n')}</code></pre>`);buf=[];}code=!code;continue;}if(code){buf.push(line);continue;}const unordered=/^[-*] /.test(line),ordered=/^\d+\. /.test(line),kind=unordered?'ul':ordered?'ol':null;if(kind){if(list!==kind){closeList();out.push(`<${kind}>`);list=kind;}out.push(`<li>${inlineMd(line.replace(unordered?/^[-*] /:/^\d+\. /,''))}</li>`);continue;}closeList();if(/^### /.test(line))out.push(`<h3>${inlineMd(line.slice(4))}</h3>`);else if(/^## /.test(line))out.push(`<h2>${inlineMd(line.slice(3))}</h2>`);else if(/^# /.test(line))out.push(`<h1>${inlineMd(line.slice(2))}</h1>`);else if(line.trim())out.push(`<p>${inlineMd(line)}</p>`);}closeList();return out.join('');}
  function inlineMd(s){return s.replace(/`([^`]+)`/g,'<code>$1</code>').replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/\*([^*]+)\*/g,'<em>$1</em>').replace(/(https?:\/\/[^\s<]+)/g,'<a href="$1" target="_blank" rel="noopener noreferrer">$1</a>');}
  let knowledgeSaveInFlight=null;
  async function saveKnowledge(){
    if(knowledgeSaveInFlight)return knowledgeSaveInFlight;
    const d=state.knowledgeDocument;if(!d)return;
    const editor=$('#knowledgeEditor'),content=editor.value;
    if(content===d.content)return;
    editor.disabled=true;
    knowledgeSaveInFlight=api(`/api/knowledge/documents/${d.id}/content`,{method:'PUT',body:JSON.stringify({content})})
      .then(()=>{d.content=content;})
      .finally(()=>{editor.disabled=false;knowledgeSaveInFlight=null;});
    return knowledgeSaveInFlight;
  }
  async function moveKnowledge(id,parentId,order){await api(`/api/knowledge/nodes/${id}/position`,{method:'PUT',body:JSON.stringify({parentId:parentId||null,order})});if(parentId)state.knowledgeExpanded.add(parentId);await loadKnowledge();}
  function closeKnowledgeContext(){document.querySelector('.knowledge-context-menu')?.remove();}
  function knowledgeContext(id,x,y){const n=knowledgeNode(id);if(!n)return;closeKnowledgeContext();const menu=document.createElement('div');menu.className='knowledge-context-menu';menu.setAttribute('role','menu');const rename=document.createElement('button');rename.type='button';rename.textContent='Переименовать';rename.addEventListener('click',async()=>{closeKnowledgeContext();const title=await requestKnowledgeName('Переименовать',n.title,'Сохранить');if(!title)return;try{await api(`/api/knowledge/nodes/${id}/name`,{method:'PATCH',body:JSON.stringify({title})});await loadKnowledge();}catch(e){showToast(e.message);}});const remove=document.createElement('button');remove.type='button';remove.textContent='Удалить';remove.addEventListener('click',async()=>{closeKnowledgeContext();if(!await requestKnowledgeDelete(n.title))return;try{await api(`/api/knowledge/nodes/${id}`,{method:'DELETE'});await loadKnowledge();}catch(e){showToast(e.message);}});menu.append(rename,remove);document.body.append(menu);positionContextMenu(menu,x,y);}

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
    const row = document.createElement('article'); row.className = 'task-row' + (task.bucket === 'today' ? ' today' : ''); row.dataset.taskRow=task.id;
    if (task.bucket === 'today') row.dataset.todayTaskRow=task.id;
    row.append(createDragHandle('task', task.id, 'Перетащить задачу'));
    const open = document.createElement('button'); open.type='button'; open.className='task-open'; open.dataset.taskOpen=task.id;
    if (task.bucket === 'today') { const dot=document.createElement('span'); dot.className=`status-dot ${statusDotClass(task.status)}`; open.append(dot); }
    const title=document.createElement('span'); title.className='task-title'; title.textContent=task.title; open.append(title);
    row.append(open);
    if (task.bucket === 'backlog') { const move=document.createElement('button'); move.type='button'; move.className='move-button'; move.dataset.taskMove=task.id; move.textContent='→'; row.append(move); }
    if (task.bucket === 'today') { const b=document.createElement('button'); b.type='button'; b.className='status-button'; b.dataset.taskAdvance=task.id; b.textContent=statusLabel[task.status]; row.append(b); }
    return row;
  }

  function createDragHandle(kind, id, label) {
    const handle=document.createElement('button'); handle.type='button'; handle.className='drag-handle';
    handle.dataset.dragKind=kind; handle.dataset.dragId=id; handle.title=label; handle.setAttribute('aria-label',label);
    if(kind==='task') handle.append(createLeafDragIcon()); else handle.textContent='☰';
    return handle;
  }

  function renderTasks() {
    const today = state.taskTab === 'today';
    $('#pageTitle').textContent = 'Задачи';
    $('#backlogTab').classList.toggle('active', !today); $('#todayTab').classList.toggle('active', today);
    $('#addForm').classList.toggle('hidden', today); $('#filters').classList.toggle('hidden', !today);
    $$('.filter').forEach(b => b.classList.toggle('active', b.dataset.filter === state.taskFilter));

    const visible = state.tasks.filter(taskVisible); const grouped = new Map();
    visible.forEach(t => { const s=normalizeSection(t.section); if(!grouped.has(s)) grouped.set(s,[]); grouped.get(s).push(t); });
    const container=$('#taskList'); container.replaceChildren();
    for (const [section, items] of grouped) {
      const group=document.createElement('section'); group.className='task-group'; group.dataset.sectionGroup=section;
      const h=document.createElement('div'); h.className='group-header';
      const toggle=document.createElement('button'); toggle.type='button'; toggle.className='group-toggle'; toggle.dataset.taskGroup=section; toggle.textContent=section;
      h.append(createDragHandle('section', section, 'Перетащить раздел'), toggle); group.append(h);
      if (state.expanded[state.taskTab].has(section)) { const list=document.createElement('div'); list.className='group-tasks'; items.forEach(t=>list.append(createTaskRow(t))); group.append(list); }
      container.append(group);
    }
    $('#emptyState').classList.toggle('hidden', visible.length !== 0);
    const sections=[...new Set(state.tasks.filter(task=>task.bucket===state.taskTab).map(task=>normalizeSection(task.section)))];
    const expanded=sections.length>0&&sections.every(section=>state.expanded[state.taskTab].has(section));
    const allToggle=$('#toggleAllSections');
    setAllSectionsToggle(allToggle,expanded,'Свернуть всё','Развернуть всё');
  }

  function showTaskDetail(task) {
    cancelDescriptionEdit();
    cancelTitleEdit();
    cancelAgentEdit();
    state.selectedTaskId=task.id; $('#listView').classList.add('hidden'); $('#detailView').classList.remove('hidden');
    $('#detailSection').textContent=normalizeSection(task.section); $('#detailTitle').textContent=task.title; $('#detailDescription').textContent=task.description;
    const actions=$('#detailActions'); actions.replaceChildren();
    const move=document.createElement('button'); move.type='button'; move.className='detail-move'; move.dataset.taskMove=task.id; move.textContent=task.bucket==='backlog'?'→ Сегодня':'← Backlog'; actions.append(move);
    if(task.bucket==='today'){const b=document.createElement('button');b.type='button';b.className='detail-status';b.dataset.taskAdvance=task.id;b.textContent=statusLabel[task.status];actions.append(b);}
    const agentEdit=document.createElement('button'); agentEdit.type='button'; agentEdit.className='detail-edit-agent'; agentEdit.dataset.taskEditAgent=task.id; agentEdit.textContent='Редактировать с агентом'; actions.append(agentEdit);
    const del=document.createElement('button');del.type='button';del.className='detail-delete';del.dataset.taskDelete=task.id;del.textContent='Удалить';actions.append(del);
  }
  function closeTaskDetail(){cancelAgentEdit();state.selectedTaskId=null;$('#detailView').classList.add('hidden');$('#listView').classList.remove('hidden');}
  async function moveTask(id){const t=state.tasks.find(x=>x.id===id);if(!t)return;const updated=await api(`/api/tasks/${id}/bucket`,{method:'PUT',body:JSON.stringify({bucket:t.bucket==='backlog'?'today':'backlog'})});Object.assign(t,updated,{section:normalizeSection(updated.section)});renderTasks();if(state.selectedTaskId===id){showTaskDetail(t);navigate({main:'tasks',taskTab:t.bucket,filter:'all',taskId:id},{replace:true});}}
  async function advanceTask(id){const t=state.tasks.find(x=>x.id===id);if(!t)return;const r=await api(`/api/tasks/${id}/advance`,{method:'PUT',body:'{}'});if(r.deleted){state.tasks=state.tasks.filter(x=>x.id!==id);if(state.selectedTaskId===id)navigate({main:'tasks',taskTab:state.taskTab,filter:state.taskFilter},{replace:true});}else Object.assign(t,r,{section:normalizeSection(r.section)});renderTasks();if(state.selectedTaskId===id&&state.tasks.includes(t))showTaskDetail(t);}
  async function deleteTask(id){if(!confirm('Удалить задачу?'))return;const t=state.tasks.find(x=>x.id===id);if(!t)return;await api(`/api/tasks/${id}`,{method:'DELETE'});state.tasks=state.tasks.filter(x=>x.id!==id);if(state.selectedTaskId===id)navigate({main:'tasks',taskTab:state.taskTab,filter:state.taskFilter},{replace:true});renderTasks();showToast('Задача удалена');}

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

  // -------- Агент-редактирование задачи (заголовок / описание / раздел) --------
  function beginAgentEdit() {
    if (state.agentEditing || !state.selectedTaskId) return;
    const task = state.tasks.find(x => x.id === state.selectedTaskId); if (!task) return;
    cancelDescriptionEdit(); cancelTitleEdit();
    const panel = document.createElement('form');
    panel.id = 'agentEditPanel'; panel.className = 'agent-edit-panel';
    const input = document.createElement('textarea');
    input.id = 'agentEditInput'; input.className = 'description-input';
    input.placeholder = 'Опишите, что изменить: заголовок, описание или раздел…';
    input.setAttribute('rows', '2');
    input.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.preventDefault(); cancelAgentEdit(); }
      else if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submitAgentEdit().catch(err => showToast(err.message)); }
    });
    const actions = document.createElement('div'); actions.className = 'agent-edit-actions';
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'modal-cancel'; cancel.textContent = 'Отмена';
    cancel.addEventListener('click', cancelAgentEdit);
    const send = document.createElement('button'); send.type = 'submit'; send.className = 'primary'; send.textContent = 'Отправить агенту';
    actions.append(cancel, send);
    panel.append(input, actions);
    panel.addEventListener('submit', e => { e.preventDefault(); submitAgentEdit().catch(err => showToast(err.message)); });
    state.agentEditing = true;
    $('#detailActions').before(panel);
    input.focus();
  }
  function cancelAgentEdit() {
    if (!state.agentEditing) return;
    $('#agentEditPanel')?.remove();
    state.agentEditing = false;
  }
  async function submitAgentEdit() {
    if (!state.agentEditing || !state.selectedTaskId) return;
    const input = $('#agentEditInput'); const id = state.selectedTaskId;
    const text = input.value.trim();
    if (!text) { input.focus(); showToast('Опишите, что нужно изменить.'); return; }
    input.disabled = true;
    try {
      const draft = await api(`/api/tasks/${id}/edit`, { method: 'POST', body: JSON.stringify({ text }) });
      cancelAgentEdit();
      openTaskDraft(draft, id);
    } catch (err) {
      input.disabled = false;
      showToast(err.message);
    }
  }

  // -------- Переименование раздела: долгое нажатие на заголовок группы --------
  const LONG_PRESS_MS = 500;
  const LONG_PRESS_TOLERANCE = 10;
  let pressTimer = null;
  let pressStart = null;
  let pressPointerId = null;
  let pressedSection = null;
  let suppressNextClick = false;

  function cancelSectionPress() {
    if (pressTimer !== null) { clearTimeout(pressTimer); pressTimer = null; }
    pressStart = null;
    pressPointerId = null;
    pressedSection = null;
  }

  function toggleSection(section) {
    const set = state.expanded[state.taskTab];
    set.has(section) ? set.delete(section) : set.add(section);
    renderTasks();
  }

  function closeSectionMenu() {
    document.querySelector('.section-context-menu')?.remove();
    document.querySelector('.group-toggle[aria-expanded="true"]')?.setAttribute('aria-expanded', 'false');
  }

  function positionContextMenu(menu, clientX, clientY) {
    const rect = menu.getBoundingClientRect(); const margin = 8; const horizontalGap = 18; const verticalGap = 28;
    const right = clientX + horizontalGap;
    const left = right + rect.width <= window.innerWidth - margin ? right : Math.max(margin, clientX - rect.width - horizontalGap);
    const above = clientY - rect.height - verticalGap;
    const top = above >= margin ? above : Math.min(clientY + verticalGap, window.innerHeight - rect.height - margin);
    menu.style.left = `${left}px`;
    menu.style.top = `${Math.max(margin, top)}px`;
  }

  function openSectionMenu(toggle, clientX, clientY) {
    closeSectionMenu();
    const menu = document.createElement('div');
    menu.className = 'section-context-menu';
    menu.setAttribute('role', 'menu');
    const rename = document.createElement('button');
    rename.type = 'button';
    rename.setAttribute('role', 'menuitem');
    rename.dataset.sectionRename = toggle.dataset.taskGroup;
    rename.textContent = 'Переименовать';
    menu.append(rename);
    document.body.append(menu);
    positionContextMenu(menu, clientX, clientY);
    toggle.setAttribute('aria-expanded', 'true');
  }

  function openRenameModal(section) {
    state.renamingSection = section;
    const input = $('#renameSectionInput');
    input.value = section;
    $('#renameSectionModal').showModal();
    input.focus();
    input.select();
  }

  function closeRenameModal() {
    const modal = $('#renameSectionModal');
    if (modal.open) modal.close();
    state.renamingSection = null;
  }

  async function saveRenameSection() {
    const oldName = state.renamingSection;
    if (!oldName) return;
    const newName = $('#renameSectionInput').value.trim();
    if (!newName) { showToast('Название не может быть пустым'); return; }
    const result = await api('/api/tasks/sections/rename', { method: 'PUT', body: JSON.stringify({ oldName, newName }) });
    if (result.renamed > 0) {
      const lower = oldName.toLowerCase();
      state.tasks.forEach(t => { if (normalizeSection(t.section).toLowerCase() === lower) t.section = newName; });
      ['backlog', 'today'].forEach(tab => {
        const set = state.expanded[tab];
        if (set.has(oldName)) { set.delete(oldName); set.add(newName); }
      });
      renderTasks();
      showToast('Раздел переименован');
    }
    closeRenameModal();
  }

  // -------- Перетаскивание задач и разделов --------
  // Реализовано через Pointer Events, поэтому одинаково работает мышью и касанием.
  let activeDrag = null;
  const TASK_DRAG_START_TOLERANCE = 4;

  function startDrag(event) {
    const handle=event.target.closest('[data-drag-kind]');
    if (!handle || event.button !== 0 || activeDrag) return;
    if (state.taskTab==='today' && state.taskFilter!=='all') {
      showToast('Для изменения порядка выберите фильтр «Все».');
      return;
    }
    const kind=handle.dataset.dragKind;
    const item=kind==='task' ? handle.closest('.task-row') : handle.closest('.task-group');
    if (!item) return;
    activeDrag={ kind, item, handle, pointerId:event.pointerId, startX:event.clientX, startY:event.clientY, started:false, moved:false,
      startSection: kind==='task' ? item.closest('.task-group')?.dataset.sectionGroup : null };
    handle.setPointerCapture?.(event.pointerId);
    event.preventDefault();
  }

  function clearTaskDropTarget(drag) {
    drag.dropTarget?.classList.remove('task-drop-before','task-drop-after','task-drop-inside');
    drag.dropTarget=null;
    drag.dropPlacement=null;
  }

  function clearTaskDrag(drag) {
    drag.item.classList.remove('dragging');
    drag.ghost?.remove();
    clearTaskDropTarget(drag);
  }

  function startTaskDragVisual(drag) {
    drag.started=true;
    drag.item.classList.add('dragging');
    const rect=drag.item.getBoundingClientRect();
    drag.offsetX=drag.startX-rect.left;
    drag.offsetY=drag.startY-rect.top;
    drag.ghost=drag.item.cloneNode(true);
    drag.ghost.classList.remove('dragging');
    drag.ghost.classList.add('task-drag-ghost');
    drag.ghost.setAttribute('aria-hidden','true');
    drag.ghost.inert=true;
    drag.ghost.style.width=`${rect.width}px`;
    document.body.append(drag.ghost);
  }

  function updateTaskDropTarget(drag,event) {
    const point=document.elementFromPoint(event.clientX,event.clientY);
    let target=null, placement=null;
    if (drag.kind==='task') {
      const row=point?.closest('.task-row');
      if (row && row!==drag.item) {
        target=row;
        const rect=row.getBoundingClientRect();
        placement=event.clientY<rect.top+rect.height/2?'before':'after';
      } else {
        const group=point?.closest('.task-group');
        if (group?.querySelector(':scope > .group-tasks')) { target=group; placement='inside'; }
      }
    } else {
      const group=point?.closest('.task-group');
      if (group && group!==drag.item && group.parentElement===$('#taskList')) {
        target=group;
        const rect=group.getBoundingClientRect();
        placement=event.clientY<rect.top+rect.height/2?'before':'after';
      }
    }
    if (target===drag.dropTarget && placement===drag.dropPlacement) return;
    clearTaskDropTarget(drag);
    drag.dropTarget=target;
    drag.dropPlacement=placement;
    if (placement==='before') target?.classList.add('task-drop-before');
    else if (placement==='after') target?.classList.add('task-drop-after');
    else if (placement==='inside') target?.classList.add('task-drop-inside');
  }

  function applyTaskDrop(drag) {
    const target=drag.dropTarget, placement=drag.dropPlacement;
    if (!target || !placement) return false;
    if (drag.kind==='task') {
      if (placement==='inside') {
        const list=target.querySelector(':scope > .group-tasks');
        if (!list) return false;
        list.append(drag.item);
      } else {
        const list=target.closest('.group-tasks');
        if (!list) return false;
        list.insertBefore(drag.item,placement==='before'?target:target.nextSibling);
      }
      return true;
    }
    const parent=$('#taskList');
    if (target.parentElement!==parent) return false;
    parent.insertBefore(drag.item,placement==='before'?target:target.nextSibling);
    return true;
  }

  function moveDrag(event) {
    const drag=activeDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    if (!drag.started) {
      if (Math.hypot(event.clientX-drag.startX,event.clientY-drag.startY)<TASK_DRAG_START_TOLERANCE) return;
      startTaskDragVisual(drag);
    }
    drag.ghost.style.left=`${event.clientX-drag.offsetX}px`;
    drag.ghost.style.top=`${event.clientY-drag.offsetY}px`;
    updateTaskDropTarget(drag,event);
    drag.moved=true;
    event.preventDefault();
  }

  async function finishDrag(event, commit) {
    const drag=activeDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    activeDrag=null;
    if (!drag.started) return;
    const validDrop=commit&&applyTaskDrop(drag);
    clearTaskDrag(drag);
    if (!validDrop) return;

    try {
      if (drag.kind==='task') {
        const list=drag.item.closest('.group-tasks');
        const group=drag.item.closest('.task-group');
        if (!list || !group) { await loadTasks(); return; }
        const section=group.dataset.sectionGroup;
        const taskIds=[...list.querySelectorAll('[data-task-row]')].map(row=>row.dataset.taskRow);
        const movedToAnotherSection = section !== drag.startSection;
        if (movedToAnotherSection)
          await api(`/api/tasks/${drag.item.dataset.taskRow}/section`,{method:'PUT',body:JSON.stringify({section})});
        await api('/api/tasks/reorder',{method:'PUT',body:JSON.stringify({bucket:state.taskTab,section,taskIds})});
        showToast(movedToAnotherSection ? `Задача перенесена в раздел «${section}»` : 'Порядок задач сохранён');
      } else {
        const sections=[...$('#taskList').querySelectorAll(':scope > [data-section-group]')].map(group=>group.dataset.sectionGroup);
        await api('/api/tasks/sections/reorder',{method:'PUT',body:JSON.stringify({bucket:state.taskTab,sections})});
        showToast('Порядок разделов сохранён');
      }
      await loadTasks();
    } catch (err) {
      await loadTasks();
      showToast(err.message);
    }
  }

  document.addEventListener('pointerdown', startDrag, true);
  document.addEventListener('pointermove', moveDrag, true);
  document.addEventListener('pointerup', event => { finishDrag(event,true); }, true);
  document.addEventListener('pointercancel', event => { finishDrag(event,false); }, true);

  document.addEventListener('pointerdown', e => {
    const g = e.target.closest('[data-task-group]');
    if (!g || e.button > 1) return;
    cancelSectionPress();
    if (e.pointerType !== 'mouse') e.preventDefault();
    pressStart = { x: e.clientX, y: e.clientY };
    pressPointerId = e.pointerId;
    pressedSection = g.dataset.taskGroup;
    pressTimer = setTimeout(() => {
      pressTimer = null;
      suppressNextClick = true;
      openSectionMenu(g, pressStart.x, pressStart.y);
    }, LONG_PRESS_MS);
  }, true);

  document.addEventListener('pointermove', e => {
    if (pressTimer && pressPointerId === e.pointerId && pressStart && Math.hypot(e.clientX - pressStart.x, e.clientY - pressStart.y) > LONG_PRESS_TOLERANCE)
      cancelSectionPress();
  }, { passive: true });

  document.addEventListener('pointerup', e => {
    if (pressTimer && pressPointerId === e.pointerId && pressedSection) {
      const section = pressedSection;
      cancelSectionPress();
      if (e.pointerType !== 'mouse') {
        suppressNextClick = true;
        toggleSection(section);
        return;
      }
    }
    cancelSectionPress();
  }, true);
  document.addEventListener('pointercancel', () => { cancelSectionPress(); suppressNextClick = false; }, true);
  document.addEventListener('contextmenu', e => {
    if (e.target.closest('[data-task-group]')) e.preventDefault();
  });

  // Один клик сразу после long-press гасится, чтобы группа не схлопнулась под меню.
  document.addEventListener('click', e => {
    if (!suppressNextClick) return;
    suppressNextClick = false;
    e.preventDefault();
    e.stopImmediatePropagation();
  }, true);

  // -------- Контекстные действия задачи --------
  let backlogTaskPress=null;
  let suppressBacklogTaskClick=false;

  function dismissBacklogTaskMenu(){document.querySelector('#backlogTaskMenu')?.remove();}
  function openBacklogTaskMenu(taskId,clientX,clientY){
    dismissBacklogTaskMenu();
    dismissTodayTaskMenu();
    const menu=document.createElement('div');menu.id='backlogTaskMenu';menu.className='task-context-menu';menu.setAttribute('role','menu');
    for(const [label,attribute] of [['Переименовать','backlogTaskRename'],['Удалить','backlogTaskDelete']]){
      const action=document.createElement('button');action.type='button';action.setAttribute('role','menuitem');action.dataset[attribute]=taskId;action.textContent=label;menu.append(action);
    }
    document.body.append(menu);positionContextMenu(menu,clientX,clientY);
  }

  document.addEventListener('pointerdown',e=>{
    if(!document.querySelector('#backlogTaskMenu')||e.target.closest('#backlogTaskMenu'))return;
    dismissBacklogTaskMenu();suppressBacklogTaskClick=true;e.preventDefault();e.stopImmediatePropagation();
  },true);
  document.addEventListener('pointerdown',e=>{
    const row=e.target.closest('[data-task-row]');
    const task=row&&state.tasks.find(item=>String(item.id)===String(row.dataset.taskRow));
    if(!task||task.bucket!=='backlog'||e.button!==0||activeDrag||e.target.closest('[data-drag-kind]'))return;
    const press={x:e.clientX,y:e.clientY,pointerId:e.pointerId,taskId:task.id,timer:null};
    press.timer=setTimeout(()=>{
      if(backlogTaskPress!==press)return;
      backlogTaskPress=null;suppressBacklogTaskClick=true;openBacklogTaskMenu(press.taskId,press.x,press.y);
    },LONG_PRESS_MS);
    backlogTaskPress=press;
  },true);
  document.addEventListener('pointermove',e=>{
    if(backlogTaskPress&&backlogTaskPress.pointerId===e.pointerId&&Math.hypot(e.clientX-backlogTaskPress.x,e.clientY-backlogTaskPress.y)>LONG_PRESS_TOLERANCE){clearTimeout(backlogTaskPress.timer);backlogTaskPress=null;}
  },{passive:true});
  document.addEventListener('pointerup',e=>{if(backlogTaskPress?.pointerId===e.pointerId){clearTimeout(backlogTaskPress.timer);backlogTaskPress=null;}},true);
  document.addEventListener('pointercancel',e=>{if(backlogTaskPress?.pointerId===e.pointerId){clearTimeout(backlogTaskPress.timer);backlogTaskPress=null;}suppressBacklogTaskClick=false;},true);
  document.addEventListener('contextmenu',e=>{if(e.target.closest('[data-task-row]'))e.preventDefault();});
  document.addEventListener('click',e=>{
    if(!suppressBacklogTaskClick)return;
    suppressBacklogTaskClick=false;e.preventDefault();e.stopImmediatePropagation();
  },true);

  // -------- Контекстное действие задачи «Сегодня» --------
  // Долгое нажатие на саму строку не затрагивает long-press заголовков разделов.
  let todayTaskPress = null;
  let suppressTodayTaskClick = false;

  function dismissTodayTaskMenu() {
    $('#todayTaskMenu')?.remove();
  }

  function cancelTodayTaskPress() {
    if (todayTaskPress?.timer != null) clearTimeout(todayTaskPress.timer);
    todayTaskPress = null;
  }

  function openTodayTaskMenu(taskId, clientX, clientY) {
    dismissTodayTaskMenu();
    dismissBacklogTaskMenu();
    const menu=document.createElement('div'); menu.id='todayTaskMenu'; menu.className='today-task-context-menu'; menu.setAttribute('role','menu');
    const action=document.createElement('button'); action.type='button'; action.className='today-task-context-action'; action.dataset.todayTaskBacklog=taskId; action.setAttribute('role','menuitem'); action.textContent='← Backlog';
    menu.append(action); document.body.append(menu);
    positionContextMenu(menu, clientX, clientY);
    action.focus({preventScroll:true});
  }

  document.addEventListener('pointerdown', e => {
    const row=e.target.closest('[data-today-task-row]');
    if (!row || e.button !== 0 || activeDrag || e.target.closest('[data-drag-kind],[data-task-advance]')) return;
    dismissTodayTaskMenu();
    const press={ x:e.clientX, y:e.clientY, taskId:row.dataset.todayTaskRow, timer:null };
    press.timer=setTimeout(() => {
      if (todayTaskPress !== press) return;
      todayTaskPress=null;
      suppressTodayTaskClick=true;
      openTodayTaskMenu(press.taskId,press.x,press.y);
    }, LONG_PRESS_MS);
    todayTaskPress=press;
  }, true);

  document.addEventListener('pointermove', e => {
    if (todayTaskPress && Math.hypot(e.clientX-todayTaskPress.x,e.clientY-todayTaskPress.y)>LONG_PRESS_TOLERANCE) cancelTodayTaskPress();
  }, { passive:true });
  document.addEventListener('pointerup', cancelTodayTaskPress, true);
  document.addEventListener('pointercancel', () => { cancelTodayTaskPress(); suppressTodayTaskClick=false; }, true);
  document.addEventListener('contextmenu', e => { if (e.target.closest('[data-today-task-row]')) e.preventDefault(); });
  document.addEventListener('pointerdown', e => {
    if (!e.target.closest('#todayTaskMenu')) dismissTodayTaskMenu();
  }, true);
  document.addEventListener('selectstart', e => {
    if (e.target.closest('[data-today-task-row]')) e.preventDefault();
  }, true);
  document.addEventListener('click', e => {
    if (suppressTodayTaskClick) {
      suppressTodayTaskClick=false;
      e.preventDefault();
      e.stopImmediatePropagation();
      return;
    }
    if (!e.target.closest('#todayTaskMenu')) dismissTodayTaskMenu();
  }, true);
  window.addEventListener('resize', dismissTodayTaskMenu);
  document.addEventListener('scroll', dismissTodayTaskMenu, true);

  document.addEventListener('pointerdown', e => {
    if (!e.target.closest('.section-context-menu')) closeSectionMenu();
  }, true);
  document.addEventListener('click', e => {
    const action = e.target.closest('[data-section-rename]');
    if (!action) return;
    e.preventDefault();
    closeSectionMenu();
    openRenameModal(action.dataset.sectionRename);
  });

  $('#renameSectionForm').addEventListener('submit', e => {
    e.preventDefault();
    saveRenameSection().catch(err => showToast(err.message));
  });
  $('#renameSectionCancel').addEventListener('click', closeRenameModal);
  $('#renameSectionModal').addEventListener('click', e => { if (e.target === $('#renameSectionModal')) closeRenameModal(); });
  $('#renameSectionModal').addEventListener('close', () => { state.renamingSection = null; });

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
    renderRecent(); renderSecondary();  }

  function setSecondary(tab){state.secondary=tab;$$('.secondary-tab').forEach(b=>b.classList.toggle('active',b.dataset.secondary===tab));renderSecondary();}
  function renderSecondary(){
    const c=$('#secondaryContent'); c.replaceChildren(); const m=state.dashboard.metrics;
    if(state.secondary==='recent'){const list=document.createElement('div');list.className='simple-list';m.recentPrepares.forEach(x=>{const d=document.createElement('div');d.className='simple-item';d.innerHTML=`<span><b>${escapeHtml(x.agent)}</b> · ${escapeHtml(projectName(x.projectId))}<br><span class="muted">${escapeHtml(x.task)}</span></span><span class="muted">${x.foundRecords} запис.</span>`;list.append(d);});c.append(list);return;}
    const src=state.secondary==='agents'?m.byAgent:m.byProject; const rows=Object.entries(src).map(([name,v])=>({name:state.secondary==='projects'?projectName(name):name,...v})).sort((a,b)=>b.applied-a.applied);const max=Math.max(1,...rows.map(x=>x.applied));const chart=document.createElement('div');chart.className='mini-chart';rows.forEach(r=>{const d=document.createElement('div');d.className='mini-row';d.innerHTML=`<div class="row-head"><span>${escapeHtml(r.name)}</span><span class="muted">${r.applied} · ✓${r.verified}</span></div><div class="bar-track"><div class="bar-fill" style="width:${Math.round(r.applied/max*100)}%"></div></div>`;chart.append(d);});c.append(chart);
  }

  function setRecentTab(tab){state.recentTab=tab;$$('[data-recent-tab]').forEach(b=>b.classList.toggle('active',b.dataset.recentTab===tab));renderRecent();}
  function renderRecent(){
    const m=state.dashboard.metrics; const src=state.recentTab==='applied'?m.recentApplied:state.recentTab==='added'?m.recentAdded:m.recentReads; const items=src||[];
    const c=$('#recentContent'); c.replaceChildren();
    if(!items.length){const d=document.createElement('div');d.className='recent-empty';d.textContent='Пока нет данных';c.append(d);return;}
    const ts=state.recentTab==='applied'?'appliedAt':state.recentTab==='added'?'addedAt':'readAt';
    const list=document.createElement('div');list.className='simple-list';
    items.slice(0,5).forEach(x=>{const d=document.createElement('div');d.className='recent-item';d.innerHTML='<span class="recent-title">'+escapeHtml(x.title||x.id)+'</span><span class="muted">'+formatTs(x[ts])+'</span>';list.append(d);});
    c.append(list);
  }

  async function searchLessons(){const p=new URLSearchParams();const q=$('#lessonSearch').value.trim(),project=$('#lessonProject').value,agent=$('#lessonAgent').value;if(q)p.set('q',q);if(project)p.set('project',project);if(agent)p.set('agent',agent);const lessons=await api('/api/memory/lessons?'+p.toString());lessons.sort(lessonSortCmp);const box=$('#lessonList');box.replaceChildren();lessons.forEach(l=>box.append(createLessonRow(l)));$('#lessonEmpty').classList.toggle('hidden',lessons.length>0);}
  function createLessonRow(l){const b=document.createElement('button');b.type='button';b.className='lesson-row';b.dataset.lessonOpen=l.id;const added=formatTs(l.occurredAt);b.innerHTML=`<div class="min-w-0"><div class="lesson-title">${escapeHtml(l.title)}</div><div class="lesson-date">${added?'Добавлено '+added:''}</div><div class="lesson-mobile-meta">${escapeHtml(l.project)} · ${escapeHtml(l.agent)} · ${escapeHtml(l.scope)}</div></div><div class="lesson-meta"><div>${escapeHtml(l.project)}</div><div>${escapeHtml(l.agent)} · ${escapeHtml(l.scope)}</div></div><div class="lesson-stats">${l.appliedCount} примен.<br>✓ ${l.verifiedCount} подтверждено</div>`;return b;}

  async function searchProblems(){const p=new URLSearchParams();const q=$('#problemSearch').value.trim(),project=$('#problemProject').value;if(q)p.set('q',q);if(project)p.set('project',project);const items=await api('/api/memory/problems?'+p.toString());state.problems=items;const box=$('#problemList');box.replaceChildren();items.forEach(p=>{const b=document.createElement('button');b.type='button';b.className='problem-row';b.dataset.problemOpen=p.id;b.innerHTML=`<div class="row-main"><div class="row-title">${escapeHtml(p.title)}</div><div class="row-sub">${escapeHtml(p.summary)}</div><div class="row-extra">${escapeHtml(p.project)} · ${p.solutionCount} решений</div></div><div class="row-side">${p.appliedCount} примен.<br>✓ ${p.verifiedCount} подтверждено</div>`;box.append(b);});$('#problemEmpty').classList.toggle('hidden',items.length>0);}

  async function searchSkills(){const p=new URLSearchParams();const q=$('#skillSearch').value.trim();if(q)p.set('q',q);const items=await api('/api/memory/skills?'+p.toString());state.skills=items;const box=$('#skillList');box.replaceChildren();items.forEach(s=>{const b=document.createElement('button');b.type='button';b.className='skill-row'+(s.status==='obsolete'?' muted':'');b.dataset.skillOpen=s.id;b.innerHTML=`<div class="row-main"><div class="row-title">${escapeHtml(s.name)}</div><div class="row-sub">${escapeHtml(s.description)}</div></div><div class="row-side">${escapeHtml(s.project)}<br>${skillStatusCap(s.status)}</div>`;box.append(b);});$('#skillEmpty').classList.toggle('hidden',items.length>0);}

  function renderSystem(){const s=state.memoryStatus;const cards=[['База',s.database],['Hindsight',s.hindsight],['Worker',s.worker],['Очередь',{status:`${s.queue.queued} queued · ${s.queue.failed} failed`,requiresAttention:s.queue.failed>0}]];$('#systemCards').innerHTML=cards.map(([name,v])=>`<div class="system-card"><div class="system-title"><span class="status-dot ${v.requiresAttention?'work':'done'}"></span>${name}</div><div class="system-sub">${escapeHtml(v.status)}</div></div>`).join('');$('#systemJobs').innerHTML=s.jobs.map(j=>`<div class="simple-item"><span>${escapeHtml(j.id)}</span><span class="muted">${escapeHtml(j.status)}</span></div>`).join('');$('#systemComputers').innerHTML=s.computers.map(c=>`<div class="simple-item"><span>${escapeHtml(c.name)}</span><span class="muted">${escapeHtml(c.status)}</span></div>`).join('');}

  function openLesson(id){const l=(state.dashboard&&state.dashboard.lessons||[]).find(x=>String(x.id)===String(id));if(!l)return false;state.selectedLesson=id;$('#memoryMain').classList.add('hidden');$('#lessonDetail').classList.remove('hidden');$('#problemDetail').classList.add('hidden');$('#skillDetail').classList.add('hidden');$('#lessonDetailProject').textContent=l.project;$('#lessonDetailScope').textContent=`${l.scope} · ${l.status}`;$('#lessonDetailTitle').textContent=l.title;$('#lessonDetailMeta').textContent=`${l.agent} · ${l.appliedCount} применений · ${l.verifiedCount} подтверждено`;$('#lessonDetailDates').textContent=`Добавлен: ${formatTs(l.occurredAt)} · Прочитан: ${formatTs(l.lastReadAt)||'—'} · Применён: ${formatTs(l.lastAppliedAt)||'—'}`;$('#lessonProblem').textContent=l.problem;$('#lessonConditions').textContent=l.conditions;$('#lessonCause').textContent=l.cause;$('#lessonMethod').textContent=l.workingMethod;$('#lessonEvidence').textContent=`${l.evidence} ${l.verification}`;return true;}
  function closeLesson(){state.selectedLesson=null;$('#lessonDetail').classList.add('hidden');$('#memoryMain').classList.remove('hidden');}
  function openProblem(id){const p=state.problems.find(x=>String(x.id)===String(id));if(!p)return;state.selectedProblem=id;$('#memoryMain').classList.add('hidden');$('#problemDetail').classList.remove('hidden');$('#lessonDetail').classList.add('hidden');$('#skillDetail').classList.add('hidden');$('#problemDetailScope').textContent=p.scope||'Общее';$('#problemDetailProject').textContent=`${p.project||projectName(p.projectId)} · ${p.solutionCount??0} решений`;$('#problemDetailTitle').textContent=p.title;$('#problemDetailMeta').textContent=`Изменена: ${formatTs(p.lastChange)||'—'}`;$('#problemDetailSummary').textContent=p.summary||'';renderProblemSolutions(p);}
  function closeProblem(){state.selectedProblem=null;$('#problemDetail').classList.add('hidden');$('#memoryMain').classList.remove('hidden');}
  function renderProblemSolutions(p){const box=$('#problemDetailSolutions');box.replaceChildren();const sols=p.solutions;if(!Array.isArray(sols)||!sols.length){const d=document.createElement('div');d.className='problem-no-solutions';d.textContent='Решения пока не загружены';box.append(d);return;}sols.forEach(sol=>box.append(createSolutionCard(sol)));}
  function lessonStatusWord(s){return s==='active'?'активен':s==='superseded'?'заменено':s==='obsolete'?'устарел':String(s??'');}
  function createSolutionCard(sol){const card=document.createElement('div');card.className='solution-card';const head=document.createElement('div');head.className='solution-head';const t=document.createElement('button');t.type='button';t.className='solution-open';t.textContent=sol.title||sol.id;t.title='Открыть урок';t.addEventListener('click',()=>{if((state.dashboard&&state.dashboard.lessons||[]).some(x=>String(x.id)===String(sol.id)))navigate({main:'memory',memoryTab:'lessons',lessonId:sol.id});else toggleSolutionInline(card,sol);});const st=document.createElement('span');st.className='solution-status '+(sol.status==='active'?'active':sol.status==='superseded'?'superseded':sol.status==='obsolete'?'obsolete':'');st.textContent=lessonStatusWord(sol.status);head.append(t,st);const stats=document.createElement('div');stats.className='solution-stats';stats.textContent=`${sol.appliedCount??0} применений · ${sol.verifiedCount??0} подтверждено`;card.append(head,stats);return card;}
  function toggleSolutionInline(card,sol){const old=card.querySelector('.solution-inline');if(old){old.remove();return;}const wrap=document.createElement('div');wrap.className='solution-inline';const note=document.createElement('div');note.className='muted small-text';note.textContent='Урок не найден в общем списке — содержимое решения:';wrap.append(note);const fields=[['Проблема',sol.problem],['Условия',sol.conditions],['Причина',sol.cause],['Рабочий метод',sol.workingMethod]];fields.forEach(([label,txt])=>{if(!txt)return;const c=document.createElement('div');c.className='description-card';const l=document.createElement('div');l.className='description-label';l.textContent=label;const pp=document.createElement('p');pp.textContent=txt;c.append(l,pp);wrap.append(c);});const ev=[];if(sol.evidence)ev.push(sol.evidence);if(sol.verification)ev.push(sol.verification);if(ev.length){const c=document.createElement('div');c.className='description-card';const l=document.createElement('div');l.className='description-label';l.textContent='Доказательство и проверка';const pp=document.createElement('p');pp.textContent=ev.join(' ');c.append(l,pp);wrap.append(c);}if(!wrap.querySelector('.description-card')){const c=document.createElement('div');c.className='muted small-text';c.textContent='Дополнительное содержимое решения недоступно';wrap.append(c);}card.append(wrap);}
  function openSkill(id){const s=state.skills.find(x=>String(x.id)===String(id));if(!s)return;state.selectedSkill=id;$('#memoryMain').classList.add('hidden');$('#skillDetail').classList.remove('hidden');$('#lessonDetail').classList.add('hidden');$('#problemDetail').classList.add('hidden');$('#skillDetailProject').textContent=s.project||projectName(s.projectId);$('#skillDetailStatus').textContent=skillStatusCap(s.status);$('#skillDetailStatus').className=`skill-status-chip ${s.status==='obsolete'?'obsolete':'active'}`;$('#skillDetailScope').textContent=s.scope||'Общее';$('#skillDetailTitle').textContent=s.name;const meta=[];meta.push(`Версия ${s.version||'—'}`);if(s.versionCount)meta.push(`всего версий: ${s.versionCount}`);meta.push(`Зарегистрирован: ${formatTs(s.registeredAt)||'—'}`);meta.push(`Обновлён: ${formatTs(s.updatedAt)||'—'}`);$('#skillDetailMeta').textContent=meta.join(' · ');$('#skillDetailDescription').textContent=s.description||'';renderSkillExample(s);renderSkillDeps(s);renderSkillComputers(s);}
  function closeSkill(){state.selectedSkill=null;$('#skillDetail').classList.add('hidden');$('#memoryMain').classList.remove('hidden');}
  function renderSkillExample(s){const card=$('#skillDetailExampleCard');const pre=$('#skillDetailExample');let val=s.verifiedExample;if(Array.isArray(val))val=val.join('\n');const has=val!==undefined&&val!==null&&String(val).trim()!=='';if(has){pre.textContent=String(val);card.classList.remove('hidden');}else{pre.textContent='';card.classList.add('hidden');}}
  function renderSkillDeps(s){const card=$('#skillDetailDepsCard');const list=$('#skillDetailDeps');list.replaceChildren();const raw=Array.isArray(s.dependencies)?s.dependencies:(typeof s.dependencies==='string'?s.dependencies.split(','):[]);const deps=raw.map(x=>String(x).trim()).filter(Boolean);if(deps.length){deps.forEach(d=>{const li=document.createElement('li');li.textContent=d;list.append(li);});card.classList.remove('hidden');}else card.classList.add('hidden');}
  function renderSkillComputers(s){const card=$('#skillDetailComputersCard');const wrap=$('#skillDetailComputers');wrap.replaceChildren();const comps=String(s.computer||'').split(',').map(x=>x.trim()).filter(Boolean);if(comps.length){comps.forEach(c=>{const chip=document.createElement('span');chip.className='computer-chip';chip.textContent=c;wrap.append(chip);});card.classList.remove('hidden');}else card.classList.add('hidden');}
  function skillStatusWord(s){return s==='obsolete'?'устарел':s==='active'?'активен':String(s??'');}
  function skillStatusCap(s){return s==='obsolete'?'Устарел':s==='active'?'Активен':String(s??'');}
  function projectName(id){if(id==='lost-cyber-hamster-2025')return'Lost Cyber Hamster';if(id==='agent-memory-system')return'Общая память агентов';if(id==='*')return'Общий опыт';return id;}
  function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[ch]));}
  function formatTs(iso){if(!iso)return'';const d=new Date(iso);if(isNaN(d.getTime()))return'';const p=n=>String(n).padStart(2,'0');return `${p(d.getDate())}.${p(d.getMonth()+1)}.${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}`;}
  function tsDiff(a,b){if(!a&&!b)return 0;if(!a)return 1;if(!b)return -1;return b.localeCompare(a);}
  function lessonSortCmp(a,b){const by=state.lessonSort;let d=0;if(by==='applied'){d=(b.appliedCount??0)-(a.appliedCount??0);if(!d)d=tsDiff(a.lastAppliedAt,b.lastAppliedAt);}else if(by==='read'){d=tsDiff(a.lastReadAt,b.lastReadAt);}else{d=tsDiff(a.occurredAt,b.occurredAt);}return d?d:String(a.title??'').localeCompare(String(b.title??''));}
  function debounce(fn,delay=220){let timer;return(...args)=>{clearTimeout(timer);timer=setTimeout(()=>fn(...args),delay);};}

  // -------- Events --------
  $('#knowledgeNameForm').addEventListener('submit',e=>{e.preventDefault();const input=$('#knowledgeNameInput');if(!input.value.trim()){input.value='';input.reportValidity();return;}$('#knowledgeNameModal').close('submit');});
  $('#knowledgeNameCancel').addEventListener('click',()=>$('#knowledgeNameModal').close('cancel'));
  $('#knowledgeDeleteCancel').addEventListener('click',()=>$('#knowledgeDeleteModal').close('cancel'));
  $('#knowledgeDeleteConfirm').addEventListener('click',()=>$('#knowledgeDeleteModal').close('delete'));
  $$('[data-main]').forEach(b=>b.addEventListener('click',async()=>{
    if(b.dataset.main==='next-section'){showToast('Этот раздел пока не реализован');return;}
    try{if(state.main==='knowledge'&&state.knowledgeEditing)await closeKnowledgeDocument();}catch(e){showToast(e.message);return;}
    navigate(b.dataset.main==='memory'
      ? {main:'memory',memoryTab:state.memoryTab}
      : b.dataset.main==='knowledge' ? {main:'knowledge'}
      : {main:'tasks',taskTab:state.taskTab,filter:state.taskFilter});
  }));
  $('#knowledgeToggleAll').addEventListener('click',()=>{const sections=knowledgeFlat(state.knowledge).filter(n=>n.kind==='section');const allExpanded=sections.length>0&&sections.every(n=>state.knowledgeExpanded.has(n.id));if(allExpanded)state.knowledgeExpanded.clear();else sections.forEach(n=>state.knowledgeExpanded.add(n.id));renderKnowledgeTree();});
  $('#knowledgeBack').addEventListener('click',()=>closeKnowledgeDocument().catch(e=>showToast(e.message)));
  $('#knowledgeModeToggle').addEventListener('click',async()=>{const button=$('#knowledgeModeToggle');if(button.disabled)return;button.disabled=true;try{if(state.knowledgeEditing)await saveKnowledge();state.knowledgeEditing=!state.knowledgeEditing;renderKnowledgeMode();if(state.knowledgeEditing)$('#knowledgeEditor').focus();}catch(e){showToast(e.message);}finally{button.disabled=false;}});
  async function closeKnowledgeDocument(){if(state.knowledgeEditing)await saveKnowledge();$('#knowledgeDocument').classList.add('hidden');$('#knowledgeTree').classList.remove('hidden');state.knowledgeDocument=null;state.knowledgeEditing=false;}
  $('#knowledgeTree').addEventListener('click',async e=>{const addS=e.target.closest('[data-knowledge-add-section]'),addD=e.target.closest('[data-knowledge-add-doc]'),row=e.target.closest('[data-knowledge-id]');if(e.target.closest('.knowledge-drag-handle'))return;try{if(addS){await createKnowledge('section',addS.dataset.knowledgeAddSection||null);return;}if(addD){await createKnowledge('document',addD.dataset.knowledgeAddDoc||null);return;}if(!row)return;const n=knowledgeNode(row.dataset.knowledgeId);if(n.kind==='section'&&e.target.closest('.knowledge-node-title')){state.knowledgeExpanded.has(n.id)?state.knowledgeExpanded.delete(n.id):state.knowledgeExpanded.add(n.id);renderKnowledgeTree();}else if(n.kind==='document'){showKnowledgeDocument(await api(`/api/knowledge/documents/${n.id}`));}}catch(err){showToast(err.message);}});
  $('#knowledgeTree').addEventListener('contextmenu',e=>{const row=e.target.closest('[data-knowledge-id]');if(row){e.preventDefault();if(e.pointerType!=='touch')knowledgeContext(row.dataset.knowledgeId,e.clientX,e.clientY);}});
  function knowledgeDropPlacement(target,clientY){
    if(!target)return null;
    if(target.hasAttribute('data-knowledge-root'))return 'root';
    const node=knowledgeNode(target.dataset.knowledgeId);if(!node)return null;
    const rect=target.getBoundingClientRect(),edge=Math.min(10,rect.height*.28);
    if(clientY<=rect.top+edge)return 'before';
    if(clientY>=rect.bottom-edge)return 'after';
    return node.kind==='section'?'inside':(clientY<rect.top+rect.height/2?'before':'after');
  }
  async function dropKnowledge(id,targetRow,root,placement){
    if(!id)return;const source=knowledgeNode(id);if(!source)return;
    let parentId=null,order=state.knowledge.length;
    if(targetRow){
      const target=knowledgeNode(targetRow.dataset.knowledgeId);if(!target||target.id===source.id||!placement)return;
      if(placement==='inside'&&target.kind==='section'){parentId=target.id;order=(target.children||[]).length;}
      else{parentId=target.parentId;order=target.order+(placement==='after'?1:0);if(source.parentId===parentId&&source.order<target.order)order--;}
    }else if(!root)return;
    try{await moveKnowledge(id,parentId,order);}catch(err){showToast(err.message);}
  }
  const KNOWLEDGE_DRAG_START_TOLERANCE = 4;
  let knowledgePress=null, suppressKnowledgeClickPoint=null;
  document.addEventListener('pointerdown',e=>{
    if(!document.querySelector('.knowledge-context-menu')||e.target.closest('.knowledge-context-menu'))return;
    closeKnowledgeContext();
    suppressKnowledgeClickPoint={x:e.clientX,y:e.clientY,until:Date.now()+700};
    e.preventDefault();
    e.stopImmediatePropagation();
  },true);
  function updateKnowledgeGhost(press,x,y){press.ghost.style.left=`${x-press.offsetX}px`;press.ghost.style.top=`${y-press.offsetY}px`;}
  function clearKnowledgeDrag(press){press.row.classList.remove('dragging');press.ghost?.remove();press.dropTarget?.classList.remove('knowledge-drop-target','knowledge-drop-before','knowledge-drop-after');}
  $('#knowledgeTree').addEventListener('pointerdown',e=>{
    const row=e.target.closest('[data-knowledge-id]');
    if(knowledgePress||!row||e.button!==0||e.target.closest('[data-knowledge-add-section],[data-knowledge-add-doc]'))return;
    closeKnowledgeContext();
    const canDrag=!!e.target.closest('.knowledge-drag-handle');
    const press={id:row.dataset.knowledgeId,row,canDrag,x:e.clientX,y:e.clientY,pointerId:e.pointerId,dragging:false,longPressed:false,menuOpen:false,timer:null,ghost:null,dropTarget:null};
    if(!canDrag)press.timer=setTimeout(()=>{if(knowledgePress!==press||press.dragging)return;press.longPressed=true;press.menuOpen=true;knowledgeContext(press.id,press.x,press.y);},LONG_PRESS_MS);
    else{e.preventDefault();e.target.setPointerCapture?.(e.pointerId);}
    knowledgePress=press;
  });
  document.addEventListener('pointermove',e=>{
    const press=knowledgePress;if(!press||press.pointerId!==e.pointerId)return;
    if(!press.dragging){
      if(Math.hypot(e.clientX-press.x,e.clientY-press.y)<=KNOWLEDGE_DRAG_START_TOLERANCE)return;
      if(!press.canDrag){clearTimeout(press.timer);knowledgePress=null;return;}
      clearTimeout(press.timer);
      if(press.menuOpen)closeKnowledgeContext();
      press.dragging=true;
      press.row.classList.add('dragging');
      const rect=press.row.getBoundingClientRect();
      press.offsetX=press.x-rect.left;press.offsetY=press.y-rect.top;
      press.ghost=press.row.cloneNode(true);
      press.ghost.classList.remove('dragging');
      press.ghost.classList.add('knowledge-drag-ghost');
      press.ghost.setAttribute('aria-hidden','true');press.ghost.inert=true;
      press.ghost.style.width=`${rect.width}px`;
      document.body.append(press.ghost);
    }
    updateKnowledgeGhost(press,e.clientX,e.clientY);
    const point=document.elementFromPoint(e.clientX,e.clientY);
    const target=point?.closest('[data-knowledge-id],[data-knowledge-root]');
    const next=target?.dataset.knowledgeId===press.id?null:target;
    const placement=next?knowledgeDropPlacement(next,e.clientY):null;
    if(next!==press.dropTarget||placement!==press.dropPlacement){
      press.dropTarget?.classList.remove('knowledge-drop-target','knowledge-drop-before','knowledge-drop-after');
      press.dropTarget=next;press.dropPlacement=placement;
      if(placement==='inside'||placement==='root')next?.classList.add('knowledge-drop-target');
      if(placement==='before')next?.classList.add('knowledge-drop-before');
      if(placement==='after')next?.classList.add('knowledge-drop-after');
    }
  },true);
  document.addEventListener('pointerup',e=>{
    const press=knowledgePress;if(!press||press.pointerId!==e.pointerId)return;
    knowledgePress=null;clearTimeout(press.timer);
    if(!press.dragging){if(press.menuOpen)suppressKnowledgeClickPoint={x:e.clientX,y:e.clientY,until:Date.now()+700};return;}
    const point=document.elementFromPoint(e.clientX,e.clientY);
    clearKnowledgeDrag(press);
    suppressKnowledgeClickPoint={x:e.clientX,y:e.clientY,until:Date.now()+700};
    const target=point?.closest('[data-knowledge-id],[data-knowledge-root]');
    const row=target?.matches('[data-knowledge-id]')?target:null;
    const root=target?.matches('[data-knowledge-root]')?target:null;
    dropKnowledge(press.id,row,root,knowledgeDropPlacement(target,e.clientY));
  },true);
  document.addEventListener('pointercancel',e=>{const press=knowledgePress;if(!press||press.pointerId!==e.pointerId)return;clearTimeout(press.timer);clearKnowledgeDrag(press);if(press.menuOpen)closeKnowledgeContext();knowledgePress=null;},true);
  document.addEventListener('selectstart',e=>{if(knowledgePress?.dragging&&e.target.closest('#knowledgeTree'))e.preventDefault();},true);
  document.addEventListener('click',e=>{const point=suppressKnowledgeClickPoint;const suppress=point&&Date.now()<point.until&&Math.hypot(e.clientX-point.x,e.clientY-point.y)<24;suppressKnowledgeClickPoint=null;if(suppress){e.preventDefault();e.stopImmediatePropagation();return;}if(!e.target.closest('.knowledge-context-menu'))closeKnowledgeContext();},true);
  $('#backlogTab').addEventListener('click',()=>navigate({main:'tasks',taskTab:'backlog',filter:'all'}));
  $('#todayTab').addEventListener('click',()=>navigate({main:'tasks',taskTab:'today',filter:'all'}));
  $$('.filter').forEach(b=>b.addEventListener('click',()=>navigate({main:'tasks',taskTab:'today',filter:b.dataset.filter})));
  $('#toggleAllSections').addEventListener('click',()=>{const sections=[...new Set(state.tasks.filter(t=>t.bucket===state.taskTab).map(t=>normalizeSection(t.section)))];const allExpanded=sections.length>0&&sections.every(section=>state.expanded[state.taskTab].has(section));if(allExpanded)state.expanded[state.taskTab].clear();else sections.forEach(section=>state.expanded[state.taskTab].add(section));renderTasks();});
  $('#backButton').addEventListener('click',()=>goBack({main:'tasks',taskTab:state.taskTab,filter:state.taskFilter}));
    $('#detailDescription').addEventListener('click', beginDescriptionEdit);
    $('#detailTitle').addEventListener('click', beginTitleEdit);
  $('#addForm').addEventListener('submit',async e=>{e.preventDefault();if(chatSending)return;const btn=$('#addForm button[type="submit"]');const text=$('#taskInput').value.trim();navigate({main:'tasks',chat:true,sessionId:null},{replace:false});if(!text)return;btn.disabled=true;btn.classList.add('sending');try{$('#taskInput').value='';await sendChatMessage(text);}catch(err){showToast(err.message);navigate({main:'tasks',taskTab:'backlog',filter:'all'},{replace:true});}finally{btn.disabled=false;btn.classList.remove('sending');}});
  $('#knowledgeChatForm').addEventListener('submit',async e=>{e.preventDefault();if(chatSending)return;const input=$('#knowledgeChatInput');const text=input.value.trim();input.value='';navigate({main:'knowledge',chat:true,sessionId:null},{replace:false});if(text)try{await sendChatMessage(text);}catch(err){showToast(err.message);}});
  $('#chatForm').addEventListener('submit',async e=>{e.preventDefault();if(chatSending)return;const input=$('#chatInput');const text=input.value.trim();if(!text)return;input.value='';try{await sendChatMessage(text);}catch(err){showToast(err.message);}});
  $('#chatBackButton').addEventListener('click',()=>navigate(chatScope==='tasks'?{main:'tasks',taskTab:'backlog',filter:'all'}:{main:'knowledge'}));
  $('#taskDraftForm').addEventListener('submit', e => { e.preventDefault(); reviseTaskDraft(); });
  $('#taskDraftConfirm').addEventListener('click', confirmTaskDraft);
  $('#taskDraftCancel').addEventListener('click', closeTaskDraft);
  $('#taskDraftCorrection').addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); reviseTaskDraft(); } });
  $('#taskDraftModal').addEventListener('cancel', e => { if ($('#taskDraftModal').getAttribute('aria-busy') === 'true') e.preventDefault(); });
  $('#taskDraftModal').addEventListener('close', () => { taskDraftSession = null; });

  $$('.memory-tab').forEach(b=>b.addEventListener('click',()=>navigate({main:'memory',memoryTab:b.dataset.memoryTab})));
  $$('.secondary-tab').forEach(b=>b.addEventListener('click',()=>setSecondary(b.dataset.secondary)));
  $('#lessonSearch').addEventListener('input',debounce(()=>searchLessons().catch(e=>showToast(e.message))));
  $('#lessonProject').addEventListener('change',()=>searchLessons().catch(e=>showToast(e.message)));
  $('#lessonAgent').addEventListener('change',()=>searchLessons().catch(e=>showToast(e.message)));
  $('#problemSearch').addEventListener('input',debounce(()=>searchProblems().catch(e=>showToast(e.message))));
  $('#problemProject').addEventListener('change',()=>searchProblems().catch(e=>showToast(e.message)));
  $('#skillSearch').addEventListener('input',debounce(()=>searchSkills().catch(e=>showToast(e.message))));
  $('#lessonBack').addEventListener('click',()=>goBack({main:'memory',memoryTab:'lessons'}));
  $('#problemDetailBack').addEventListener('click',()=>goBack({main:'memory',memoryTab:'problems'}));
  $('#skillDetailBack').addEventListener('click',()=>goBack({main:'memory',memoryTab:'skills'}));
  $$('[data-lesson-sort]').forEach(b=>b.addEventListener('click',()=>{state.lessonSort=b.dataset.lessonSort;$$('[data-lesson-sort]').forEach(x=>x.classList.toggle('active',x===b));searchLessons().catch(e=>showToast(e.message));}));
  $$('[data-recent-tab]').forEach(b=>b.addEventListener('click',()=>setRecentTab(b.dataset.recentTab)));

  document.addEventListener('click',async e=>{
    try{
      if(e.target.closest('[data-drag-kind]'))return;
      const rename=e.target.closest('[data-backlog-task-rename]');if(rename){const id=rename.dataset.backlogTaskRename;dismissBacklogTaskMenu();const task=state.tasks.find(item=>String(item.id)===String(id));if(task){navigate({main:'tasks',taskTab:task.bucket,filter:'all',taskId:task.id});requestAnimationFrame(beginTitleEdit);}return;}
      const remove=e.target.closest('[data-backlog-task-delete]');if(remove){const id=remove.dataset.backlogTaskDelete;dismissBacklogTaskMenu();await deleteTask(id);return;}
      const back=e.target.closest('[data-today-task-backlog]');if(back){dismissTodayTaskMenu();await moveTask(back.dataset.todayTaskBacklog);return;}
      const g=e.target.closest('[data-task-group]');if(g){closeSectionMenu();toggleSection(g.dataset.taskGroup);return;}
      const o=e.target.closest('[data-task-open]');if(o){const t=state.tasks.find(x=>x.id===o.dataset.taskOpen);if(t)navigate({main:'tasks',taskTab:t.bucket,filter:'all',taskId:t.id});return;}
      const m=e.target.closest('[data-task-move]');if(m){await moveTask(m.dataset.taskMove);return;}
      const a=e.target.closest('[data-task-advance]');if(a){await advanceTask(a.dataset.taskAdvance);return;}
      const ae=e.target.closest('[data-task-edit-agent]');if(ae){beginAgentEdit();return;}
      const d=e.target.closest('[data-task-delete]');if(d){await deleteTask(d.dataset.taskDelete);return;}
      const l=e.target.closest('[data-lesson-open]');if(l){navigate({main:'memory',memoryTab:'lessons',lessonId:l.dataset.lessonOpen});return;}
      const pr=e.target.closest('[data-problem-open]');if(pr){navigate({main:'memory',memoryTab:'problems',problemId:pr.dataset.problemOpen});return;}
      const sk=e.target.closest('[data-skill-open]');if(sk){navigate({main:'memory',memoryTab:'skills',skillId:sk.dataset.skillOpen});return;}
    }catch(err){showToast(err.message);}
  });

  window.addEventListener('popstate', applyCurrentRoute);
  window.addEventListener('hashchange', applyCurrentRoute);
  initBottomNavGestures();

  async function init(){try{await Promise.all([loadTasks(),loadMemory(),loadKnowledge()]);setSecondary('agents');applyCurrentRoute();}catch(err){showToast(err.message);}}
  init();
})();
