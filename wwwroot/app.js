(() => {
  const state = {
    tasks: [],
    tab: 'backlog',
    filter: 'all',
    selectedId: null
  };

  const $ = (selector) => document.querySelector(selector);
  const listView = $('#listView');
  const detailView = $('#detailView');
  const taskList = $('#taskList');
  const pageTitle = $('#pageTitle');
  const addForm = $('#addForm');
  const taskInput = $('#taskInput');
  const filters = $('#filters');
  const emptyState = $('#emptyState');
  const toast = $('#toast');

  const statusLabel = {
    new: 'В работу',
    in_progress: 'Завершить',
    completed: 'Стикер'
  };

  function statusDotClass(status) {
    if (status === 'in_progress') return 'work';
    if (status === 'completed') return 'done';
    return 'new';
  }

  function showToast(message) {
    toast.textContent = message;
    toast.classList.remove('hidden');
    window.clearTimeout(showToast.timer);
    showToast.timer = window.setTimeout(() => toast.classList.add('hidden'), 1800);
  }

  async function api(url, options = {}) {
    const response = await fetch(url, {
      ...options,
      headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
    });
    if (!response.ok) {
      let message = 'Ошибка запроса.';
      try { message = (await response.json()).message || message; } catch { }
      throw new Error(message);
    }
    if (response.status === 204) return null;
    return response.json();
  }

  async function loadTasks() {
    try {
      state.tasks = await api('/api/tasks');
      render();
    } catch (error) {
      showToast(error.message);
    }
  }

  function setTab(tab) {
    state.tab = tab;
    state.filter = 'all';
    state.selectedId = null;
    showList();
    render();
  }

  function showList() {
    detailView.classList.add('hidden');
    listView.classList.remove('hidden');
  }

  function showDetail(task) {
    state.selectedId = task.id;
    listView.classList.add('hidden');
    detailView.classList.remove('hidden');
    $('#detailTitle').textContent = task.title;
    $('#detailDescription').textContent = task.description;

    const actions = $('#detailActions');
    actions.replaceChildren();

    const move = document.createElement('button');
    move.type = 'button';
    move.className = 'detail-move';
    move.dataset.action = 'move';
    move.dataset.id = task.id;
    move.textContent = task.bucket === 'backlog' ? '→ Сегодня' : '← Backlog';
    actions.append(move);

    if (task.bucket === 'today') {
      const status = document.createElement('button');
      status.type = 'button';
      status.className = 'detail-status';
      status.dataset.action = 'advance';
      status.dataset.id = task.id;
      status.textContent = statusLabel[task.status];
      actions.append(status);
    }
  }

  function createTaskRow(task) {
    const row = document.createElement('article');
    row.className = 'task-row' + (task.bucket === 'today' ? ' today' : '');

    const open = document.createElement('button');
    open.type = 'button';
    open.className = 'task-open';
    open.dataset.action = 'open';
    open.dataset.id = task.id;

    if (task.bucket === 'today') {
      const dot = document.createElement('span');
      dot.className = `status-dot ${statusDotClass(task.status)}`;
      dot.setAttribute('aria-label', task.status === 'new' ? 'Новая' : task.status === 'in_progress' ? 'В работе' : 'Завершена');
      open.append(dot);
    }

    const title = document.createElement('span');
    title.className = 'task-title';
    title.textContent = task.title;
    open.append(title);

    const move = document.createElement('button');
    move.type = 'button';
    move.className = 'move-button';
    move.dataset.action = 'move';
    move.dataset.id = task.id;
    move.textContent = task.bucket === 'backlog' ? '→' : '←';
    move.title = task.bucket === 'backlog' ? 'Перенести в Сегодня' : 'Перенести в Backlog';
    move.setAttribute('aria-label', move.title);

    row.append(open, move);

    if (task.bucket === 'today') {
      const status = document.createElement('button');
      status.type = 'button';
      status.className = 'status-button';
      status.dataset.action = 'advance';
      status.dataset.id = task.id;
      status.textContent = statusLabel[task.status];
      row.append(status);
    }

    return row;
  }

  function render() {
    const isToday = state.tab === 'today';
    pageTitle.textContent = isToday ? 'Сегодня' : 'Backlog';
    $('#backlogTab').classList.toggle('active', !isToday);
    $('#todayTab').classList.toggle('active', isToday);
    addForm.classList.toggle('hidden', isToday);
    filters.classList.toggle('hidden', !isToday);

    document.querySelectorAll('.filter').forEach(button => {
      button.classList.toggle('active', button.dataset.filter === state.filter);
    });

    const visible = state.tasks.filter(task => {
      if (task.bucket !== state.tab) return false;
      return !isToday || state.filter === 'all' || task.status === state.filter;
    });

    taskList.replaceChildren(...visible.map(createTaskRow));
    emptyState.classList.toggle('hidden', visible.length !== 0);
  }

  async function moveTask(id) {
    const task = state.tasks.find(item => item.id === id);
    if (!task) return;
    const bucket = task.bucket === 'backlog' ? 'today' : 'backlog';
    try {
      const updated = await api(`/api/tasks/${id}/bucket`, {
        method: 'PUT',
        body: JSON.stringify({ bucket })
      });
      Object.assign(task, updated);
      render();
      if (state.selectedId === id) showDetail(task);
    } catch (error) {
      showToast(error.message);
    }
  }

  async function advanceTask(id) {
    const task = state.tasks.find(item => item.id === id);
    if (!task) return;
    try {
      const result = await api(`/api/tasks/${id}/advance`, { method: 'PUT', body: '{}' });
      if (result.deleted) {
        state.tasks = state.tasks.filter(item => item.id !== id);
        if (state.selectedId === id) {
          state.selectedId = null;
          showList();
        }
      } else {
        Object.assign(task, result);
        if (state.selectedId === id) showDetail(task);
      }
      render();
    } catch (error) {
      showToast(error.message);
    }
  }

  addForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const text = taskInput.value.trim();
    if (!text) {
      taskInput.focus();
      return;
    }
    try {
      const created = await api('/api/tasks', {
        method: 'POST',
        body: JSON.stringify({ text })
      });
      state.tasks.unshift(created);
      taskInput.value = '';
      render();
    } catch (error) {
      showToast(error.message);
    }
  });

  $('#backlogTab').addEventListener('click', () => setTab('backlog'));
  $('#todayTab').addEventListener('click', () => setTab('today'));
  $('#backButton').addEventListener('click', () => {
    state.selectedId = null;
    showList();
  });

  filters.addEventListener('click', event => {
    const button = event.target.closest('.filter');
    if (!button) return;
    state.filter = button.dataset.filter;
    render();
  });

  document.addEventListener('click', event => {
    const control = event.target.closest('[data-action]');
    if (!control) return;
    const id = control.dataset.id;
    if (control.dataset.action === 'open') {
      const task = state.tasks.find(item => item.id === id);
      if (task) showDetail(task);
    } else if (control.dataset.action === 'move') {
      moveTask(id);
    } else if (control.dataset.action === 'advance') {
      advanceTask(id);
    }
  });

  loadTasks();
})();
