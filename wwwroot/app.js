'use strict';

/* ============================ утилиты ============================ */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

const PRIORITIES = ['Низкий', 'Обычный', 'Высокий', 'Срочный'];

let state = { boards: [], columns: [], cards: [] };
let activeBoardId = null;
let query = '';
let dragCardId = null;
let suppressClick = false;

const filters = { priorities: new Set(), tags: new Set() };
let updateInfo = null;
/** Доступна ли синхронизация в этой сборке (в «лёгкой» версии её нет). */
let syncSupported = true;

/** Карточки с развёрнутым чек-листом (показывается больше пунктов, чем в превью). */
const expandedCards = new Set();
const CHECK_PREVIEW = 5;

/* ============================ API ============================ */

async function request(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    cache: 'no-store',
  });

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const data = await res.json();
      if (data && data.error) message = data.error;
    } catch { /* тело не JSON */ }
    throw new Error(message);
  }

  if (res.status === 204) return null;
  const type = res.headers.get('content-type') || '';
  return type.includes('application/json') ? res.json() : null;
}

const api = {
  state: () => request('GET', '/api/state'),

  createBoard: (name) => request('POST', '/api/boards', { name }),
  updateBoard: (id, patch) => request('PATCH', `/api/boards/${id}`, patch),
  deleteBoard: (id) => request('DELETE', `/api/boards/${id}`),

  createColumn: (boardId, name) => request('POST', '/api/columns', { boardId, name }),
  updateColumn: (id, patch) => request('PATCH', `/api/columns/${id}`, patch),
  deleteColumn: (id) => request('DELETE', `/api/columns/${id}`),

  createCard: (payload) => request('POST', '/api/cards', payload),
  updateCard: (id, patch) => request('PATCH', `/api/cards/${id}`, patch),
  deleteCard: (id) => request('DELETE', `/api/cards/${id}`),
  moveCard: (id, columnId, index) => request('POST', `/api/cards/${id}/move`, { columnId, index }),

  archiveCard: (id) => request('POST', `/api/cards/${id}/archive`, {}),
  unarchiveCard: (id) => request('POST', `/api/cards/${id}/unarchive`, {}),

  saveChecklist: (id, items) => request('PUT', `/api/cards/${id}/checklist`, { items }),
  addLink: (id, title, url) => request('POST', `/api/cards/${id}/attachments/link`, { title, url }),
  deleteAttachment: (id) => request('DELETE', `/api/attachments/${id}`),

  backup: () => request('POST', '/api/backup', {}),
  info: () => request('GET', '/api/info'),
  quit: () => request('POST', '/api/quit', {}),
  checkUpdate: (force) => request('GET', '/api/update/check' + (force ? '?force=1' : '')),
  updateProgress: () => request('GET', '/api/update/progress'),
  applyUpdate: () => request('POST', '/api/update/apply', {}),

  syncStatus: () => request('GET', '/api/sync/status'),
  syncSave: (payload) => request('PUT', '/api/sync/config', payload),
  syncTest: () => request('POST', '/api/sync/test', {}),
  syncRun: (mode = 'auto') => request('POST', '/api/sync/run', { mode }),
  syncResolve: (mode) => request('POST', '/api/sync/resolve', { mode }),
};

/** Загрузка файла вложения: тело запроса — сами байты, имя передаётся в query. */
async function uploadAttachment(cardId, file) {
  const res = await fetch(`/api/cards/${cardId}/attachments/file?name=${encodeURIComponent(file.name)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream' },
    body: file,
  });
  if (!res.ok) {
    let message = `Не удалось загрузить «${file.name}» (${res.status})`;
    try {
      const data = await res.json();
      if (data && data.error) message = data.error;
    } catch { /* тело не JSON */ }
    throw new Error(message);
  }
  return res.json();
}

/* ============================ загрузка и отрисовка ============================ */

async function refresh() {
  setSaveState('…');
  try {
    state = await api.state();
    if (!state.boards.some((b) => b.id === activeBoardId)) {
      activeBoardId = state.boards.length ? state.boards[0].id : null;
    }
    render();
    setSaveState('сохранено ' + new Date().toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }));
  } catch (err) {
    setSaveState('ошибка', true);
    toast('Не удалось загрузить данные: ' + err.message, 'err');
  }
}

async function mutate(action, successMessage) {
  try {
    await action();
    await refresh();
    if (successMessage) toast(successMessage, 'ok');
    return true;
  } catch (err) {
    toast(err.message, 'err');
    await refresh();
    return false;
  }
}

function setSaveState(text, isError = false) {
  const node = $('#saveState');
  node.textContent = text;
  node.classList.toggle('err', isError);
}

function render() {
  renderTabs();
  renderBoard();
  renderFilters();
  renderStatus();
  if ($('#cardDialog').open) {
    renderChecklist();
    renderAttachments();
  }
  if ($('#archiveDialog').open) renderArchive();
}

function renderStatus() {
  const board = currentBoard();
  if (!board) {
    $('#statusText').textContent = 'нет досок';
    return;
  }

  const cards = boardCards(board.id);
  const archived = archivedCards(board.id).length;
  $('#statusText').textContent =
    `${board.name} · колонок: ${boardColumns(board.id).length} · карточек: ${cards.length}` +
    (archived ? ` · в архиве: ${archived}` : '');
}

function renderTabs() {
  const tabs = $('#boardTabs');
  tabs.innerHTML = '';
  for (const board of state.boards) {
    const b = document.createElement('button');
    b.className = 'tab' + (board.id === activeBoardId ? ' active' : '');
    b.textContent = board.name;
    b.title = board.name;
    b.onclick = () => {
      activeBoardId = board.id;
      localStorage.setItem('kanban.board', String(board.id));
      render();
    };
    b.ondblclick = () => renameBoard(board);
    tabs.appendChild(b);
  }
}

function renderBoard() {
  const boardEl = $('#board');
  const scrollLeft = boardEl.scrollLeft;
  boardEl.innerHTML = '';

  const board = currentBoard();
  if (!board) {
    const empty = document.createElement('div');
    empty.className = 'empty-board';
    empty.innerHTML = '<h2>Досок пока нет</h2><p>Создайте первую доску, чтобы начать работу.</p>';
    const btn = document.createElement('button');
    btn.className = 'btn primary';
    btn.textContent = 'Создать доску';
    btn.onclick = createBoard;
    empty.appendChild(btn);
    boardEl.appendChild(empty);
    return;
  }

  const columns = boardColumns(board.id);
  for (const column of columns) boardEl.appendChild(renderColumn(column));

  const add = document.createElement('button');
  add.className = 'btn';
  add.style.cssText = 'flex:0 0 auto;margin-top:2px';
  add.textContent = '+ Колонка';
  add.onclick = () => addColumn(board.id);
  boardEl.appendChild(add);

  boardEl.scrollLeft = scrollLeft;
}

function renderColumn(column) {
  const col = document.createElement('section');
  col.className = 'column';
  col.dataset.id = String(column.id);

  const cards = columnCards(column.id).filter(isVisible);

  const head = document.createElement('header');
  head.className = 'column-head';

  const dot = document.createElement('span');
  dot.className = 'col-color';
  head.appendChild(dot);

  const name = document.createElement('span');
  name.className = 'col-name';
  name.textContent = column.name;
  name.title = 'Двойной клик — переименовать';
  name.ondblclick = () => renameColumn(column, name);
  head.appendChild(name);

  const count = document.createElement('span');
  count.className = 'col-count';
  const total = columnCards(column.id).length;
  count.textContent = column.wipLimit ? `${total}/${column.wipLimit}` : String(total);
  if (column.wipLimit && total > column.wipLimit) count.classList.add('over');
  head.appendChild(count);

  const menuBtn = document.createElement('button');
  menuBtn.className = 'col-menu';
  menuBtn.textContent = '⋯';
  menuBtn.title = 'Действия с колонкой';
  menuBtn.onclick = (e) => {
    e.stopPropagation();
    columnMenu(column, menuBtn);
  };
  head.appendChild(menuBtn);

  col.appendChild(head);

  const list = document.createElement('div');
  list.className = 'column-cards';
  list.dataset.columnId = String(column.id);
  list.setAttribute('role', 'list');
  list.setAttribute('aria-label', 'Карточки: ' + column.name);

  for (const card of cards) list.appendChild(renderCard(card, column));

  if (cards.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'column-empty';
    empty.textContent = isFiltering() ? 'ничего не найдено' : 'Перетащите карточку сюда';
    list.appendChild(empty);
  }

  col.appendChild(list);

  // важно: обработчики вешаем после вставки в DOM, иначе closest('.column') === null
  wireDropTarget(list, column);

  const foot = document.createElement('div');
  foot.className = 'column-foot';
  const add = document.createElement('button');
  add.className = 'add-card';
  add.textContent = '+ Добавить карточку';
  add.onclick = () => openComposer(list, column);
  foot.appendChild(add);
  col.appendChild(foot);

  return col;
}

function renderCard(card, column) {
  const node = document.createElement('article');
  node.className = 'card';
  node.dataset.id = String(card.id);
  node.dataset.prio = String(card.priority);
  node.draggable = true;
  node.tabIndex = 0;
  node.setAttribute('role', 'listitem');
  node.setAttribute('aria-label', card.title);

  const title = document.createElement('h3');
  title.className = 'card-title';
  title.textContent = card.title;
  node.appendChild(title);

  if (card.description.trim()) {
    const desc = document.createElement('p');
    desc.className = 'card-desc';
    desc.textContent = card.description;
    node.appendChild(desc);
  }

  const checklist = renderCardChecklist(card);
  if (checklist) node.appendChild(checklist);

  const meta = document.createElement('div');
  meta.className = 'card-meta';

  for (const tag of splitTags(card.tags)) {
    const chip = document.createElement('span');
    chip.className = 'chip tag';
    chip.textContent = '#' + tag;
    chip.title = 'Показать карточки с этим тегом';
    chip.onclick = (e) => {
      e.stopPropagation();
      toggleTagFilter(tag.toLowerCase());
    };
    meta.appendChild(chip);
  }

  if (card.dueDate) {
    const due = dueState(card.dueDate);
    const chip = document.createElement('span');
    chip.className = 'chip due' + (due.cls ? ' ' + due.cls : '');
    chip.textContent = '⚑ ' + due.text;
    chip.title = 'Срок: ' + due.full;
    meta.appendChild(chip);
  }

  if (card.priority > 0) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.textContent = PRIORITIES[card.priority];
    meta.appendChild(chip);
  }

  const attachments = card.attachments || [];
  if (attachments.length) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.textContent = `📎 ${attachments.length}`;
    chip.title = 'Вложения';
    meta.appendChild(chip);
  }

  if (meta.children.length) node.appendChild(meta);

  node.addEventListener('click', () => {
    if (suppressClick) { suppressClick = false; return; }
    openCardDialog(card, column);
  });

  node.addEventListener('keydown', (e) => {
    if (e.target !== node) return;
    if (e.key === 'Enter' && !e.ctrlKey) { e.preventDefault(); openCardDialog(card, column); }
  });

  node.addEventListener('dragstart', (e) => {
    dragCardId = card.id;
    suppressClick = true;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', String(card.id));
    requestAnimationFrame(() => node.classList.add('dragging'));
  });

  node.addEventListener('dragend', () => {
    node.classList.remove('dragging');
    dragCardId = null;
    $$('.column.drop-target').forEach((c) => c.classList.remove('drop-target'));
    setTimeout(() => { suppressClick = false; }, 60);
  });

  return node;
}

/** Чек-лист прямо на карточке: превью пунктов, прогресс и отметка без открытия диалога. */
function renderCardChecklist(card) {
  const items = card.checklist || [];
  if (!items.length) return null;

  const total = items.length;
  const done = items.filter((i) => i.done).length;
  const expanded = expandedCards.has(card.id);
  const visible = expanded ? total : Math.min(CHECK_PREVIEW, total);

  const box = document.createElement('div');
  box.className = 'card-check-list' + (done === total ? ' complete' : '');

  const head = document.createElement('div');
  head.className = 'card-check-head';

  const label = document.createElement('span');
  label.textContent = done === total ? 'Чек-лист выполнен' : 'Чек-лист';

  const counter = document.createElement('span');
  counter.className = 'card-check-count';
  counter.textContent = `${done}/${total}`;
  counter.title = `Выполнено ${done} из ${total}`;

  head.append(label, counter);
  box.appendChild(head);

  const bar = document.createElement('div');
  bar.className = 'card-check-bar';
  const fill = document.createElement('span');
  fill.style.width = `${Math.round((done / total) * 100)}%`;
  bar.appendChild(fill);
  box.appendChild(bar);

  const list = document.createElement('ul');
  list.className = 'card-check-items';

  for (let index = 0; index < visible; index++) {
    const item = items[index];

    const row = document.createElement('label');
    row.className = 'card-check' + (item.done ? ' done' : '');
    row.dataset.index = String(index);
    row.title = item.done ? 'Снять отметку' : 'Отметить выполненным';

    // клик по строке отмечает пункт, но не открывает карточку
    row.addEventListener('click', (e) => e.stopPropagation());

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!item.done;
    cb.addEventListener('change', () => {
      toggleChecklistItem(card.id, index, cb.checked);
    });

    const text = document.createElement('span');
    text.className = 'card-check-text';
    text.textContent = item.text;

    row.append(cb, text);
    list.appendChild(row);
  }

  box.appendChild(list);

  if (total > CHECK_PREVIEW) {
    const more = document.createElement('button');
    more.type = 'button';
    more.className = 'card-check-more';
    more.textContent = expanded ? 'свернуть' : `ещё ${total - CHECK_PREVIEW}`;
    more.onclick = (e) => {
      e.stopPropagation();
      if (expanded) expandedCards.delete(card.id);
      else expandedCards.add(card.id);
      renderBoard();
    };
    box.appendChild(more);
  }

  return box;
}

/** Мгновенно обновляет блок чек-листа на доске, не перерисовывая всю карточку. */
function syncCardChecklist(card) {
  const node = document.querySelector(`.card[data-id="${CSS.escape(String(card.id))}"]`);
  const box = node ? node.querySelector('.card-check-list') : null;
  if (!box) return;

  const items = card.checklist || [];
  const total = items.length;
  const done = items.filter((i) => i.done).length;

  box.classList.toggle('complete', total > 0 && done === total);
  box.querySelector('.card-check-head > span').textContent = done === total ? 'Чек-лист выполнен' : 'Чек-лист';
  const counter = box.querySelector('.card-check-count');
  counter.textContent = `${done}/${total}`;
  counter.title = `Выполнено ${done} из ${total}`;
  box.querySelector('.card-check-bar > span').style.width = `${total ? Math.round((done / total) * 100) : 0}%`;

  for (const row of box.querySelectorAll('.card-check')) {
    const item = items[Number(row.dataset.index)];
    const isDone = !!(item && item.done);
    row.classList.toggle('done', isDone);
    row.title = isDone ? 'Снять отметку' : 'Отметить выполненным';
  }
}

/** Отметка пункта чек-листа прямо на карточке (без открытия диалога). */
async function toggleChecklistItem(cardId, index, done) {
  const card = findCard(cardId);
  if (!card || !(card.checklist || [])[index]) return;

  const items = card.checklist.map((x, i) => ({ text: x.text, done: i === index ? !!done : !!x.done }));

  // оптимистично: сразу показываем результат, затем сохраняем
  card.checklist = items;
  syncCardChecklist(card);

  setSaveState('…');
  try {
    await api.saveChecklist(cardId, items);
    setSaveState('сохранено ' + new Date().toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }));
  } catch (err) {
    toast(err.message, 'err');
    await refresh();
  }
}

/* ============================ drag & drop ============================ */

function wireDropTarget(list, column) {
  const colEl = list.closest('.column');

  list.addEventListener('dragover', (e) => {
    if (dragCardId == null) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    colEl.classList.add('drop-target');

    const dragging = $('.card.dragging', document);
    if (!dragging) return;

    const empty = $('.column-empty', list);
    if (empty) empty.remove();

    const siblings = $$('.card:not(.dragging)', list);
    let before = null;
    for (const node of siblings) {
      const rect = node.getBoundingClientRect();
      if (e.clientY < rect.top + rect.height / 2) { before = node; break; }
    }

    if (before) list.insertBefore(dragging, before);
    else list.appendChild(dragging);
  });

  list.addEventListener('dragleave', (e) => {
    if (!list.contains(e.relatedTarget)) colEl.classList.remove('drop-target');
  });

  list.addEventListener('drop', async (e) => {
    e.preventDefault();
    colEl.classList.remove('drop-target');
    if (dragCardId == null) return;

    const id = dragCardId;
    const order = $$('.card', list).map((n) => Number(n.dataset.id));
    const index = Math.max(0, order.indexOf(id));
    dragCardId = null;

    await mutate(() => api.moveCard(id, column.id, index));
  });
}

/* ============================ поиск и фильтры ============================ */

function isFiltering() {
  return !!query || filters.priorities.size > 0 || filters.tags.size > 0;
}

function isVisible(card) {
  if (card.archivedAt) return false;
  return matchesQuery(card) && matchesFilters(card);
}

function matchesQuery(card) {
  if (!query) return true;
  return query
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => cardHaystack(card).includes(word));
}

function cardHaystack(card) {
  const parts = [card.title, card.description, card.tags];
  for (const item of card.checklist || []) parts.push(item.text);
  for (const att of card.attachments || []) parts.push(att.title, att.url || '');
  return parts.join(' ').toLowerCase();
}

function matchesFilters(card) {
  if (filters.priorities.size && !filters.priorities.has(card.priority)) return false;
  if (filters.tags.size) {
    const tags = splitTags(card.tags).map((t) => t.toLowerCase());
    for (const tag of filters.tags) if (!tags.includes(tag)) return false;
  }
  return true;
}

function renderFilters() {
  const board = currentBoard();
  const tags = board ? boardTags(board.id) : [];

  // Если тег исчез с доски, убираем его из фильтра — иначе доска «залипнет» пустой.
  for (const tag of [...filters.tags]) {
    if (!tags.some((t) => t.toLowerCase() === tag)) filters.tags.delete(tag);
  }

  const prioBox = $('#filterPriorities');
  prioBox.innerHTML = '';
  for (let i = 0; i < PRIORITIES.length; i++) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'chip-btn prio-' + i + (filters.priorities.has(i) ? ' on' : '');
    btn.textContent = PRIORITIES[i];
    btn.onclick = () => {
      if (filters.priorities.has(i)) filters.priorities.delete(i);
      else filters.priorities.add(i);
      saveFilters();
      renderBoard();
      renderFilters();
      renderStatus();
    };
    prioBox.appendChild(btn);
  }

  const tagBox = $('#filterTags');
  tagBox.innerHTML = '';
  if (!tags.length) {
    const empty = document.createElement('span');
    empty.className = 'muted';
    empty.textContent = 'на этой доске нет тегов';
    tagBox.appendChild(empty);
  }
  for (const tag of tags) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'chip-btn' + (filters.tags.has(tag.toLowerCase()) ? ' on' : '');
    btn.textContent = '#' + tag;
    btn.onclick = () => toggleTagFilter(tag.toLowerCase());
    tagBox.appendChild(btn);
  }

  const count = filters.priorities.size + filters.tags.size;
  const badge = $('#filterCount');
  badge.textContent = String(count);
  badge.classList.toggle('hidden', count === 0);
  $('#btnFilters').classList.toggle('active', count > 0);
}

function toggleTagFilter(tag) {
  if (filters.tags.has(tag)) filters.tags.delete(tag);
  else filters.tags.add(tag);
  saveFilters();
  renderBoard();
  renderFilters();
  renderStatus();
}

function countActiveFilters() {
  return filters.priorities.size + filters.tags.size;
}

function clearFilters() {
  filters.priorities.clear();
  filters.tags.clear();
  saveFilters();
  renderBoard();
  renderFilters();
  renderStatus();
}

function saveFilters() {
  try {
    localStorage.setItem('kanban.filters', JSON.stringify({
      priorities: [...filters.priorities],
      tags: [...filters.tags],
    }));
  } catch { /* приватный режим */ }
}

function loadFilters() {
  try {
    const raw = localStorage.getItem('kanban.filters');
    if (!raw) return;
    const data = JSON.parse(raw);
    for (const p of data.priorities || []) if (Number.isInteger(p) && p >= 0 && p <= 3) filters.priorities.add(p);
    for (const t of data.tags || []) if (typeof t === 'string' && t) filters.tags.add(t.toLowerCase());
  } catch { /* повреждённые настройки */ }
  $('#btnFilters').classList.toggle('active', countActiveFilters() > 0);
}

/* ============================ данные ============================ */

const currentBoard = () => state.boards.find((b) => b.id === activeBoardId) ?? null;
const boardColumns = (boardId) =>
  state.columns.filter((c) => c.boardId === boardId).sort((a, b) => a.position - b.position || a.id - b.id);
const columnCards = (columnId) =>
  state.cards.filter((c) => c.columnId === columnId && !c.archivedAt).sort((a, b) => a.position - b.position || a.id - b.id);
const boardCards = (boardId) => state.cards.filter((c) => c.boardId === boardId && !c.archivedAt);
const archivedCards = (boardId) =>
  state.cards
    .filter((c) => c.boardId === boardId && c.archivedAt)
    .sort((a, b) => String(b.archivedAt).localeCompare(String(a.archivedAt)));
const splitTags = (tags) => (tags ? tags.split(',').map((t) => t.trim()).filter(Boolean) : []);

function boardTags(boardId) {
  const set = new Set();
  for (const card of boardCards(boardId)) for (const tag of splitTags(card.tags)) set.add(tag);
  return [...set].sort((a, b) => a.localeCompare(b, 'ru'));
}

/** Все теги всех карточек: ключ в нижнем регистре, значение — как тег записан в базе. */
function tagIndex() {
  const map = new Map();
  for (const card of state.cards) {
    for (const tag of splitTags(card.tags)) {
      const key = tag.toLowerCase();
      if (!map.has(key)) map.set(key, tag);
    }
  }
  return map;
}

/** Последние три тега: берём из недавно изменённых карточек. */
function recentTags(limit = 3) {
  const seen = new Set();
  const result = [];

  const cards = [...state.cards].sort((a, b) => String(b.updatedAt).localeCompare(String(a.updatedAt)));
  for (const card of cards) {
    for (const tag of splitTags(card.tags)) {
      const key = tag.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      result.push(tag);
      if (result.length >= limit) return result;
    }
  }
  return result;
}

function findCard(id) {
  return state.cards.find((c) => c.id === id) ?? null;
}

function columnName(id) {
  const col = state.columns.find((c) => c.id === id);
  return col ? col.name : '';
}

function dueState(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  const today = new Date();
  today.setHours(0, 0, 0, 0);

  const diffDays = Math.round((date - today) / 86400000);
  const full = date.toLocaleDateString('ru-RU', { day: '2-digit', month: 'long', year: 'numeric' });
  const short = date.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });

  if (diffDays < 0) return { cls: 'overdue', text: short, full };
  if (diffDays === 0) return { cls: 'soon', text: 'сегодня', full };
  if (diffDays === 1) return { cls: 'soon', text: 'завтра', full };
  return { cls: '', text: short, full };
}

function formatDate(iso) {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

/* ============================ действия: доски ============================ */

async function createBoard() {
  const name = await ask({ title: 'Новая доска', label: 'Название', placeholder: 'Например, Работа' });
  if (name === null) return;
  const created = await mutate(() => api.createBoard(name || 'Новая доска'));
  if (created) {
    const board = state.boards[state.boards.length - 1];
    if (board) {
      activeBoardId = board.id;
      localStorage.setItem('kanban.board', String(board.id));
      render();
    }
  }
}

function renameBoard(board) {
  ask({ title: 'Переименовать доску', label: 'Название', value: board.name }).then((name) => {
    if (name) mutate(() => api.updateBoard(board.id, { name }));
  });
}

async function deleteBoard(board) {
  if (!confirm(`Удалить доску «${board.name}» вместе со всеми карточками?`)) return;
  await mutate(() => api.deleteBoard(board.id), 'Доска удалена');
}

/* ============================ действия: колонки ============================ */

async function addColumn(boardId) {
  const name = await ask({ title: 'Новая колонка', label: 'Название', placeholder: 'Например, Проверка' });
  if (name === null) return;
  await mutate(() => api.createColumn(boardId, name || 'Колонка'));
}

function renameColumn(column, node) {
  node.contentEditable = 'plaintext-only';
  node.focus();
  const range = document.createRange();
  range.selectNodeContents(node);
  const sel = window.getSelection();
  sel.removeAllRanges();
  sel.addRange(range);

  const finish = (save) => {
    node.contentEditable = 'false';
    const value = node.textContent.trim();
    if (save && value && value !== column.name) {
      mutate(() => api.updateColumn(column.id, { name: value }));
    } else {
      node.textContent = column.name;
    }
  };

  node.onblur = () => finish(true);
  node.onkeydown = (e) => {
    if (e.key === 'Enter') { e.preventDefault(); node.blur(); }
    if (e.key === 'Escape') { e.preventDefault(); node.onblur = null; finish(false); }
  };
}

function columnMenu(column, anchor) {
  const rect = anchor.getBoundingClientRect();
  const columns = boardColumns(column.boardId);
  const idx = columns.findIndex((c) => c.id === column.id);

  showMenu(rect.left, rect.bottom + 6, [
    { text: 'Переименовать', action: () => {
        const node = $(`.column[data-id="${column.id}"] .col-name`);
        if (node) renameColumn(column, node);
      } },
    { text: 'Лимит WIP…', hint: column.wipLimit ? String(column.wipLimit) : 'нет', action: async () => {
        const value = await ask({
          title: 'Лимит WIP',
          label: 'Максимум карточек (пусто — без лимита)',
          value: column.wipLimit ? String(column.wipLimit) : '',
          type: 'number',
        });
        if (value === null) return;
        const limit = value === '' ? 0 : Math.max(0, parseInt(value, 10) || 0);
        mutate(() => api.updateColumn(column.id, limit ? { wipLimit: limit } : { clearWipLimit: true }));
      } },
    { sep: true },
    { text: 'Переместить влево', disabled: idx <= 0,
      action: () => mutate(() => api.updateColumn(column.id, { position: idx - 1 })) },
    { text: 'Переместить вправо', disabled: idx < 0 || idx >= columns.length - 1,
      action: () => mutate(() => api.updateColumn(column.id, { position: idx + 1 })) },
    { sep: true },
    { text: 'Удалить колонку', danger: true, action: async () => {
        const count = columnCards(column.id).length;
        const warn = count ? ` Вместе с ней будут удалены карточки: ${count}.` : '';
        if (!confirm(`Удалить колонку «${column.name}»?${warn}`)) return;
        mutate(() => api.deleteColumn(column.id), 'Колонка удалена');
      } },
  ]);
}

/* ============================ действия: карточки ============================ */

function openComposer(list, column) {
  if ($('.composer', list)) return;

  const wrap = document.createElement('div');
  wrap.className = 'composer';

  const area = document.createElement('textarea');
  area.placeholder = 'Что нужно сделать?';
  area.rows = 2;

  const actions = document.createElement('div');
  actions.className = 'composer-actions';

  const add = document.createElement('button');
  add.className = 'btn primary';
  add.textContent = 'Добавить';

  const cancel = document.createElement('button');
  cancel.className = 'btn';
  cancel.textContent = 'Отмена';

  const hint = document.createElement('div');
  hint.className = 'hint-sm';
  hint.textContent = 'Enter — добавить, Shift+Enter — новая строка';

  actions.append(add, cancel);
  wrap.append(area, actions, hint);
  list.appendChild(wrap);
  area.focus();

  const close = () => wrap.remove();

  const submit = async () => {
    const title = area.value.trim();
    if (!title) { close(); return; }
    const ok = await mutate(() => api.createCard({ columnId: column.id, title, priority: 1 }));
    if (!ok) return;
    close();
    const newList = $(`.column[data-id="${column.id}"] .column-cards`);
    if (newList) {
      const btn = newList.closest('.column')?.querySelector('.add-card');
      if (btn) btn.focus();
    }
  };

  add.onclick = submit;
  cancel.onclick = close;
  area.onkeydown = (e) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); }
    if (e.key === 'Escape') { e.preventDefault(); close(); }
  };
}

let editingCardId = null;
let editingColumn = null;

function openCardDialog(card, column) {
  editingCardId = card ? card.id : null;
  editingColumn = column;

  const dlg = $('#cardDialog');
  const isNew = !card;

  $('#cardDialogTitle').textContent = isNew ? 'Новая карточка' : 'Карточка';
  $('#cardSubtitle').textContent = column ? column.name : '';
  $('#fTitle').value = card ? card.title : '';
  $('#fDesc').value = card ? card.description : '';
  $('#fPriority').value = String(card ? card.priority : 1);
  $('#fDue').value = card && card.dueDate ? card.dueDate : '';
  $('#fTags').value = card ? card.tags : '';
  $('#tagSuggest').classList.add('hidden');
  $('#tagSuggest').innerHTML = '';
  renderTagQuick();

  $('#btnDeleteCard').style.display = isNew ? 'none' : '';
  $('#btnArchiveCard').style.display = isNew ? 'none' : '';

  renderChecklist();
  renderAttachments();

  dlg.showModal();
  setTimeout(() => { $('#fTitle').focus(); $('#fTitle').select(); }, 30);
}

/** Данные карточки, открытой в диалоге (всегда свежие из state). */
function openCard() {
  return editingCardId ? findCard(editingCardId) : null;
}

function renderChecklist() {
  const card = openCard();
  const items = card ? card.checklist || [] : [];
  const list = $('#checklist');
  list.innerHTML = '';

  const empty = $('#checklistEmpty');
  $('#btnAddCheck').disabled = !card;
  if (!card) {
    empty.textContent = 'Сохраните карточку, чтобы добавить пункты.';
    empty.classList.remove('hidden');
  } else if (!items.length) {
    empty.textContent = 'Пунктов пока нет — добавьте первый.';
    empty.classList.remove('hidden');
  } else {
    empty.classList.add('hidden');
  }

  items.forEach((item, index) => {
    const li = document.createElement('li');
    li.className = 'check-item' + (item.done ? ' done' : '');

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!item.done;
    cb.title = item.done ? 'Снять отметку' : 'Отметить выполненным';
    cb.onchange = () => {
      const next = items.map((x, i) => ({ text: x.text, done: i === index ? cb.checked : !!x.done }));
      saveChecklist(next);
    };

    const text = document.createElement('input');
    text.type = 'text';
    text.className = 'check-text';
    text.maxLength = 300;
    text.value = item.text;
    text.onkeydown = (e) => {
      if (e.key === 'Enter') { e.preventDefault(); text.blur(); }
    };
    text.onblur = () => {
      const value = text.value.trim();
      if (!value) { renderChecklist(); return; }
      if (value === item.text) return;
      const next = items.map((x, i) => ({ text: i === index ? value : x.text, done: !!x.done }));
      saveChecklist(next);
    };

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'icon-btn';
    del.title = 'Удалить пункт';
    del.textContent = '✕';
    del.onclick = () => saveChecklist(items.filter((_, i) => i !== index).map((x) => ({ text: x.text, done: !!x.done })));

    li.append(cb, text, del);
    list.appendChild(li);
  });

  const total = items.length;
  const done = items.filter((i) => i.done).length;
  $('#checkProgress').textContent = total ? `${done}/${total}` : '';
}

async function saveChecklist(items) {
  if (!editingCardId) return;
  try {
    await api.saveChecklist(editingCardId, items);
    await refresh();
  } catch (err) {
    toast(err.message, 'err');
    await refresh();
  }
}

async function addChecklistItem() {
  const card = openCard();
  if (!card) return;

  const text = await ask({ title: 'Новый пункт', label: 'Что нужно сделать', placeholder: 'Например, согласовать макет' });
  if (!text) return;

  const items = (card.checklist || []).map((x) => ({ text: x.text, done: !!x.done }));
  items.push({ text, done: false });
  await saveChecklist(items);
}

function renderAttachments() {
  const card = openCard();
  const items = card ? card.attachments || [] : [];
  const list = $('#attachments');
  list.innerHTML = '';

  const empty = $('#attachEmpty');
  $('#btnAddLink').disabled = !card;
  $('#btnAddFile').disabled = !card;
  if (!card) {
    empty.textContent = 'Сохраните карточку, чтобы добавить вложения.';
    empty.classList.remove('hidden');
  } else if (!items.length) {
    empty.textContent = 'Ссылки и файлы до 25 МБ.';
    empty.classList.remove('hidden');
  } else {
    empty.classList.add('hidden');
  }

  for (const att of items) {
    const li = document.createElement('li');
    li.className = 'att-item';

    const icon = document.createElement('span');
    icon.className = 'att-icon';
    icon.textContent = att.kind === 'file' ? '📄' : '🔗';

    const link = document.createElement('a');
    link.className = 'att-title';
    link.textContent = att.title || att.originalName || 'вложение';
    if (att.kind === 'file') {
      link.href = att.downloadUrl;
      link.title = 'Скачать файл';
    } else {
      link.href = att.url || '#';
      link.target = '_blank';
      link.rel = 'noreferrer';
      link.title = att.url || '';
    }

    const meta = document.createElement('span');
    meta.className = 'att-meta';
    if (att.kind === 'file') meta.textContent = formatSize(att.size);
    else meta.textContent = att.url || '';

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'icon-btn';
    del.title = 'Удалить вложение';
    del.textContent = '✕';
    del.onclick = () => deleteAttachment(att);

    li.append(icon, link, meta, del);
    list.appendChild(li);
  }
}

async function deleteAttachment(att) {
  if (!confirm(`Удалить вложение «${att.title}»?`)) return;
  try {
    await api.deleteAttachment(att.id);
    await refresh();
    toast('Вложение удалено', 'ok');
  } catch (err) {
    toast(err.message, 'err');
  }
}

function askLink() {
  const dlg = $('#linkDialog');
  const form = $('#linkForm');
  $('#linkTitle').value = '';
  $('#linkUrl').value = '';
  $('#linkTitle').placeholder = 'Например, макет в Figma';

  return new Promise((resolve) => {
    let done = false;
    const finish = (result) => {
      if (done) return;
      done = true;
      resolve(result);
      if (dlg.open) dlg.close();
    };

    form.onsubmit = (e) => {
      e.preventDefault();
      const url = $('#linkUrl').value.trim();
      if (!url) return;
      finish({ title: $('#linkTitle').value.trim(), url });
    };
    $('[data-close]', form).onclick = () => finish(null);
    dlg.onclose = () => finish(null);

    dlg.showModal();
    setTimeout(() => $('#linkTitle').focus(), 30);
  });
}

async function addLink() {
  const card = openCard();
  if (!card) return;

  const data = await askLink();
  if (!data) return;

  try {
    await api.addLink(card.id, data.title, data.url);
    await refresh();
    toast('Ссылка добавлена', 'ok');
  } catch (err) {
    toast(err.message, 'err');
  }
}

async function addFiles(fileList) {
  const card = openCard();
  if (!card) return;

  const files = Array.from(fileList || []);
  if (!files.length) return;

  for (const file of files) {
    try {
      await uploadAttachment(card.id, file);
    } catch (err) {
      toast(err.message, 'err');
    }
  }

  await refresh();
  toast(files.length === 1 ? 'Файл добавлен' : `Добавлено файлов: ${files.length}`, 'ok');
}

async function saveCard(event) {
  event.preventDefault();
  const payload = {
    title: $('#fTitle').value.trim() || 'Без названия',
    description: $('#fDesc').value,
    priority: Number($('#fPriority').value),
    dueDate: $('#fDue').value || '',
    tags: $('#fTags').value,
  };

  const isNew = !editingCardId;
  const ok = await mutate(
    () => (isNew
      ? api.createCard({ columnId: editingColumn.id, ...payload })
      : api.updateCard(editingCardId, payload)),
    isNew ? 'Карточка добавлена' : null,
  );

  if (ok) $('#cardDialog').close();
}

async function deleteEditingCard() {
  const card = openCard();
  if (!card) return;
  if (!confirm(`Удалить карточку «${card.title}»?`)) return;
  const ok = await mutate(() => api.deleteCard(card.id), 'Карточка удалена');
  if (ok) $('#cardDialog').close();
}

async function archiveEditingCard() {
  const card = openCard();
  if (!card) return;
  const ok = await mutate(() => api.archiveCard(card.id), 'Карточка в архиве');
  if (ok) $('#cardDialog').close();
}

/* ============================ архив ============================ */

function openArchive() {
  renderArchive();
  $('#archiveDialog').showModal();
}

function renderArchive() {
  const box = $('#archiveList');
  box.innerHTML = '';

  const board = currentBoard();
  const cards = board ? archivedCards(board.id) : state.cards.filter((c) => c.archivedAt);

  if (!cards.length) {
    const p = document.createElement('p');
    p.className = 'hint-left';
    p.textContent = 'Архив пуст.';
    box.appendChild(p);
    return;
  }

  for (const card of cards) {
    const row = document.createElement('div');
    row.className = 'archive-item';

    const info = document.createElement('div');
    info.className = 'archive-info';

    const title = document.createElement('strong');
    title.textContent = card.title;

    const sub = document.createElement('span');
    sub.className = 'muted';
    sub.textContent = [
      columnName(card.columnId) || 'колонка удалена',
      card.archivedAt ? 'в архиве с ' + formatDate(card.archivedAt) : '',
    ].filter(Boolean).join(' · ');

    info.append(title, sub);

    const restore = document.createElement('button');
    restore.type = 'button';
    restore.className = 'btn sm';
    restore.textContent = 'Вернуть';
    restore.onclick = async () => {
      const ok = await mutate(() => api.unarchiveCard(card.id), 'Карточка возвращена на доску');
      if (ok) renderArchive();
    };

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'btn sm danger';
    remove.textContent = 'Удалить';
    remove.onclick = async () => {
      if (!confirm(`Удалить карточку «${card.title}» навсегда?`)) return;
      const ok = await mutate(() => api.deleteCard(card.id), 'Карточка удалена');
      if (ok) renderArchive();
    };

    row.append(info, restore, remove);
    box.appendChild(row);
  }
}

/* ============================ меню ============================ */

function showMenu(x, y, items) {
  const menu = $('#menu');
  menu.innerHTML = '';

  for (const item of items) {
    if (item.sep) {
      const sep = document.createElement('div');
      sep.className = 'menu-sep';
      menu.appendChild(sep);
      continue;
    }

    const btn = document.createElement('button');
    btn.className = 'menu-item' + (item.danger ? ' danger' : '');
    btn.type = 'button';
    btn.disabled = !!item.disabled;

    const label = document.createElement('span');
    label.textContent = item.text;
    btn.appendChild(label);

    if (item.hint) {
      const hint = document.createElement('span');
      hint.className = 'muted';
      hint.textContent = item.hint;
      btn.appendChild(hint);
    }

    btn.onclick = () => { hideMenu(); item.action?.(); };
    menu.appendChild(btn);
  }

  menu.classList.remove('hidden');
  menu.style.left = '0px';
  menu.style.top = '0px';
  const rect = menu.getBoundingClientRect();
  const left = Math.min(x, window.innerWidth - rect.width - 8);
  const top = Math.min(y, window.innerHeight - rect.height - 8);
  menu.style.left = Math.max(8, left) + 'px';
  menu.style.top = Math.max(8, top) + 'px';
}

function hideMenu() {
  $('#menu').classList.add('hidden');
}

function mainMenu(anchor) {
  const rect = anchor.getBoundingClientRect();
  const board = currentBoard();
  const archived = board ? archivedCards(board.id).length : 0;

  showMenu(rect.right - 260, rect.bottom + 6, [
    { text: 'Новая доска', action: createBoard },
    { text: 'Переименовать доску', disabled: !board, action: () => board && renameBoard(board) },
    { text: 'Удалить доску', danger: true, disabled: !board || state.boards.length < 2,
      action: () => board && deleteBoard(board) },
    { sep: true },
    { text: 'Архив карточек…', hint: archived ? String(archived) : '', disabled: !board, action: openArchive },
    { sep: true },
    ...(syncSupported ? [
      { text: 'Синхронизировать сейчас', action: () => runSync('auto') },
      { text: 'Настройки синхронизации…', action: openSyncDialog },
      { sep: true },
    ] : []),
    { text: 'Создать резервную копию', action: backupNow },
    { text: 'Экспорт в JSON…', action: exportJson },
    { text: 'Экспорт в CSV…', hint: 'Excel', action: exportCsv },
    { text: 'Экспорт с вложениями…', hint: '.zip', action: exportZip },
    { text: 'Импорт (JSON или ZIP)…', action: importData },
    { sep: true },
    { text: 'Увеличить шрифт', hint: 'Ctrl +', action: () => changeFontScale(FONT_STEP) },
    { text: 'Уменьшить шрифт', hint: 'Ctrl −', action: () => changeFontScale(-FONT_STEP) },
    { text: 'Сбросить масштаб', hint: '100%', action: resetFontScale },
    { sep: true },
    { text: 'Проверить обновления', action: () => checkUpdates(true) },
    { text: 'Открыть папку с данными', action: () => request('POST', '/api/reveal', {}).catch(() => {}) },
    { text: 'О приложении', action: showInfo },
    { sep: true },
    { text: 'Выход', danger: true, action: quitApp },
  ]);
}

/* ============================ диалоги ============================ */

function ask({ title, label, value = '', placeholder = '', type = 'text' }) {
  const dlg = $('#promptDialog');
  const input = $('#promptInput');
  const form = $('#promptForm');

  $('#promptTitle').textContent = title;
  $('#promptLabel').textContent = label;
  input.type = type;
  input.value = value;
  input.placeholder = placeholder;
  input.min = type === 'number' ? '0' : '';

  return new Promise((resolve) => {
    let done = false;
    const finish = (result) => {
      if (done) return;
      done = true;
      resolve(result);
      if (dlg.open) dlg.close();
    };

    form.onsubmit = (e) => { e.preventDefault(); finish(input.value.trim()); };
    $('[data-close]', form).onclick = () => finish(null);
    dlg.onclose = () => finish(null);

    dlg.showModal();
    setTimeout(() => { input.focus(); input.select(); }, 30);
  });
}

/* ============================ подсказки тегов ============================ */

let tagSuggestItems = [];
let tagSuggestActive = -1;
/** true, когда открыт список «Последние теги» (запрос пустой). */
let tagSuggestRecent = true;
/** Добавляет тег в поле «Теги». replaceLast — заменить недописанный последний тег. */
function insertTag(input, tag, replaceLast = false) {
  const tags = splitTags(input.value);
  if (replaceLast && tags.length) tags.pop();
  tags.push(tag);
  input.value = tags.join(', ') + ', ';

  const end = input.value.length;
  input.setSelectionRange(end, end);
}

/** Видимые чипы «Последние теги» под полем: три последних, ещё не добавленных. */
function renderTagQuick() {
  const box = $('#tagQuick');
  const list = $('#tagQuickList');
  const input = $('#fTags');
  if (!box || !list || !input) return;

  const used = new Set(splitTags(input.value).map((t) => t.toLowerCase()));
  const tags = recentTags(6).filter((tag) => !used.has(tag.toLowerCase())).slice(0, 3);

  list.innerHTML = '';
  if (!tags.length) {
    box.classList.add('hidden');
    return;
  }

  for (const tag of tags) {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'tag-quick-chip';
    chip.textContent = tag;
    chip.title = 'Добавить тег «' + tag + '»';
    chip.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      input.focus();
      insertTag(input, tag);
      $('#tagSuggest').classList.add('hidden');
      renderTagQuick();
    });
    list.appendChild(chip);
  }

  box.classList.remove('hidden');
}

/** Автодополнение тегов в карточке: последние три и фильтрация по введённым буквам. */
function setupTagSuggest() {
  const input = $('#fTags');
  const box = $('#tagSuggest');
  if (!input || !box) return;

  const currentQuery = () => {
    const value = input.value;
    const index = value.lastIndexOf(',');
    return (index >= 0 ? value.slice(index + 1) : value).trim();
  };

  const selectedKeys = () => new Set(splitTags(input.value).map((t) => t.toLowerCase()));

  function closeSuggest() {
    box.classList.add('hidden');
    box.innerHTML = '';
    tagSuggestItems = [];
    tagSuggestActive = -1;
  }

  function applyTag(tag) {
    insertTag(input, tag, !tagSuggestRecent && currentQuery().length > 0);
    closeSuggest();
    renderTagQuick();
    input.focus();
  }

  function setActive(index) {
    const nodes = $$('.tag-suggest-item', box);
    nodes.forEach((node) => node.classList.remove('active'));
    if (index >= 0 && nodes[index]) {
      nodes[index].classList.add('active');
      nodes[index].scrollIntoView({ block: 'nearest' });
    }
    tagSuggestActive = index;
  }

  function build(fresh = false) {
    const all = tagIndex();
    let query = currentQuery();
    let q = query.toLowerCase();

    // При фокусе на уже заполненном поле последний тег — это готовый тег,
    // а не поисковый запрос: показываем последние теги, как для пустого поля.
    if (fresh && q && all.has(q)) {
      query = '';
      q = '';
    }

    const used = selectedKeys();

    let list;
    let header = null;

    if (!q) {
      list = recentTags(6).filter((tag) => !used.has(tag.toLowerCase())).slice(0, 3);
      header = 'Последние теги';
    } else {
      list = [...all.values()]
        .filter((tag) => !used.has(tag.toLowerCase()) && tag.toLowerCase().includes(q))
        .sort((a, b) => {
          const ai = a.toLowerCase().indexOf(q);
          const bi = b.toLowerCase().indexOf(q);
          return ai !== bi ? ai - bi : a.localeCompare(b, 'ru');
        })
        .slice(0, 8);
    }

    tagSuggestItems = list;
    tagSuggestActive = -1;
    tagSuggestRecent = !q;
    box.innerHTML = '';

    if (!list.length) {
      if (q) {
        // Такого тега ещё нет — подсказываем, что он будет создан.
        const empty = document.createElement('div');
        empty.className = 'tag-suggest-empty';
        empty.textContent = 'Новый тег — добавится после сохранения карточки';
        box.appendChild(empty);
        box.classList.remove('hidden');
      } else {
        closeSuggest();
      }
      return;
    }

    if (header) {
      const head = document.createElement('div');
      head.className = 'tag-suggest-head';
      head.textContent = header;
      box.appendChild(head);
    }

    list.forEach((tag, index) => {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'tag-suggest-item';
      item.setAttribute('role', 'option');
      item.textContent = tag;
      item.addEventListener('mousedown', (e) => { e.preventDefault(); applyTag(tag); });
      item.addEventListener('mouseenter', () => setActive(index));
      box.appendChild(item);
    });

    box.classList.remove('hidden');
  }

  input.addEventListener('focus', () => build(true));
  input.addEventListener('click', () => build(true));
  input.addEventListener('input', () => {
    build();
    renderTagQuick();
  });

  input.addEventListener('keydown', (e) => {
    const open = !box.classList.contains('hidden');

    if (e.key === 'Escape') {
      if (!open) return;
      e.preventDefault();
      e.stopPropagation();
      closeSuggest();
      return;
    }

    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (!tagSuggestItems.length) return;
      e.preventDefault();
      const delta = e.key === 'ArrowDown' ? 1 : -1;
      let next = tagSuggestActive + delta;
      if (next < 0) next = tagSuggestItems.length - 1;
      if (next >= tagSuggestItems.length) next = 0;
      setActive(next);
      return;
    }

    if (e.key === 'Tab' && tagSuggestActive >= 0) {
      e.preventDefault();
      applyTag(tagSuggestItems[tagSuggestActive]);
      return;
    }

    if (e.key === 'Enter') {
      const query = currentQuery();
      // Пока список не тронут стрелками и поле пустое — Enter сохраняет карточку как раньше.
      if (tagSuggestActive >= 0) {
        e.preventDefault();
        applyTag(tagSuggestItems[tagSuggestActive]);
      } else if (query && tagSuggestItems.length) {
        e.preventDefault();
        applyTag(tagSuggestItems[0]);
      }
    }
  });

  input.addEventListener('blur', () => {
    // даём сработать mousedown по подсказке
    setTimeout(closeSuggest, 120);
  });
}

async function showInfo() {
  const dlg = $('#infoDialog');
  const body = $('#infoBody');
  body.innerHTML = '<p>загрузка…</p>';
  dlg.showModal();

  try {
    const info = await api.info();
    body.innerHTML = '';

    const row = (key, value) => {
      const div = document.createElement('div');
      div.className = 'info-row';
      const k = document.createElement('span');
      k.className = 'k';
      k.textContent = key;
      const v = document.createElement('span');
      v.className = 'v';
      v.textContent = value;
      div.append(k, v);
      body.appendChild(div);
    };

    row('Версия', info.version);
    row('Адрес', `http://127.0.0.1:${info.port}/`);
    row('Папка программы', info.appDir);
    row('База данных', `${info.dbPath} (${formatSize(info.dbSize)})`);
    row('Файлы вложений', `${info.filesCount} шт. (${formatSize(info.filesSize)})`);
    row('Папка обновлений', info.updateDir);

    const links = document.createElement('div');
    links.className = 'info-links';
    for (const [label, href] of [
      ['Репозиторий на GitHub', 'https://github.com/beslan-ha/kanban-portable'],
      ['Релизы и обновления', 'https://github.com/beslan-ha/kanban-portable/releases'],
    ]) {
      const a = document.createElement('a');
      a.href = href;
      a.target = '_blank';
      a.rel = 'noopener noreferrer';
      a.textContent = label;
      links.appendChild(a);
    }
    body.appendChild(links);

    const h = document.createElement('h3');
    h.textContent = 'Резервные копии';
    body.appendChild(h);

    if (!info.backups.length) {
      const p = document.createElement('p');
      p.className = 'v';
      p.textContent = 'копий пока нет';
      body.appendChild(p);
    } else {
      const list = document.createElement('ul');
      list.className = 'backup-list';
      for (const b of info.backups) {
        const li = document.createElement('li');
        li.textContent = `${b.name} · ${b.date} · ${formatSize(b.size)}`;
        list.appendChild(li);
      }
      body.appendChild(list);
    }

    const note = document.createElement('p');
    note.className = 'v';
    note.style.cssText = 'font-family:inherit;color:var(--muted)';
    note.textContent = 'Для переноса на другой ПК скопируйте всю папку программы целиком (Kanban.exe, kanban.db, files, backups).';
    body.appendChild(note);
  } catch (err) {
    body.innerHTML = '';
    const p = document.createElement('p');
    p.textContent = 'Не удалось получить сведения: ' + err.message;
    body.appendChild(p);
  }
}

function formatSize(bytes) {
  if (!bytes) return '0 Б';
  const units = ['Б', 'КБ', 'МБ', 'ГБ'];
  const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  return (bytes / Math.pow(1024, i)).toFixed(i ? 1 : 0) + ' ' + units[i];
}

/* ============================ сервисные действия ============================ */

async function backupNow() {
  try {
    const data = await api.backup();
    toast('Резервная копия: backups\\' + data.file, 'ok');
  } catch (err) {
    toast('Не удалось создать копию: ' + err.message, 'err');
  }
}

function download(url) {
  const link = document.createElement('a');
  link.href = url;
  link.download = '';
  document.body.appendChild(link);
  link.click();
  link.remove();
}

function exportJson() {
  download('/api/export');
}

function exportCsv() {
  download('/api/export/csv');
}

function exportZip() {
  download('/api/export/zip');
}

function importData() {
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = '.json,.zip,application/json,application/zip';
  input.onchange = async () => {
    const file = input.files?.[0];
    if (!file) return;
    if (!confirm(`Импортировать «${file.name}»?\n\nТекущие доски будут заменены. Перед заменой автоматически создаётся резервная копия.`)) return;

    try {
      const res = await fetch('/api/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/octet-stream' },
        body: file,
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || res.statusText);
      await refresh();
      toast(`Импортировано: досок ${data.boards}, карточек ${data.cards}`, 'ok');
    } catch (err) {
      toast('Импорт не выполнен: ' + err.message, 'err');
    }
  };
  input.click();
}

async function quitApp() {
  if (!confirm('Закрыть приложение?\n\nДоска будет недоступна до следующего запуска Kanban.exe.')) return;
  try { await api.quit(); } catch { /* сервер уже остановился */ }
  document.body.innerHTML = '<div class="empty-board" style="grid-row:1/-1"><h2>Приложение остановлено</h2>' +
    '<p>Можно закрыть эту вкладку. Для продолжения работы запустите Kanban.exe.</p></div>';
}

/* ============================ обновление из папки ============================ */

let dismissedUpdate = null;

async function checkUpdates(notifyIfNone) {
  try {
    const info = await api.checkUpdate(!!notifyIfNone);
    updateInfo = info;

    if (info.available) {
      const notes = info.notes ? ' — ' + info.notes : '';
      const where = info.source === 'github' ? 'На GitHub вышла версия' : 'В папке updates лежит версия';
      $('#updateText').textContent =
        `${where} ${info.version} (у вас ${info.currentVersion})${notes}`;
      if (dismissedUpdate !== info.version) $('#updateBanner').classList.remove('hidden');
    } else {
      $('#updateBanner').classList.add('hidden');
      if (notifyIfNone) {
        if (info.error) toast('Обновление: ' + info.error, 'err');
        else toast('Обновлений нет — установлена последняя версия ' + info.currentVersion);
      }
    }
  } catch (err) {
    if (notifyIfNone) toast('Не удалось проверить обновления: ' + err.message, 'err');
  }
}

async function applyUpdate() {
  if (!updateInfo?.available) return;

  const sizeMb = updateInfo.size ? Math.round(updateInfo.size / 1024 / 1024) : 0;
  const sizeText = sizeMb ? ` (${sizeMb} МБ)` : '';
  if (!confirm(`Установить версию ${updateInfo.version}${sizeText} и перезапустить приложение?`)) return;

  const overlay = document.createElement('div');
  overlay.className = 'overlay';
  overlay.innerHTML =
    '<div class="overlay-box">' +
      '<h2 id="updTitle">Скачиваем обновление…</h2>' +
      '<div class="upd-bar" id="updBar"><span></span></div>' +
      '<p class="upd-text" id="updText">Подготовка…</p>' +
    '</div>';
  document.body.appendChild(overlay);

  const bar = overlay.querySelector('#updBar > span');
  const title = overlay.querySelector('#updTitle');
  const text = overlay.querySelector('#updText');

  let finished = false;
  const progressTimer = setInterval(async () => {
    if (finished) return;

    try {
      const p = await api.updateProgress();
      if (!p || finished) return;

      if (p.phase === 'download') {
        const total = p.total || updateInfo.size || 0;
        const percent = total > 0 ? Math.min(100, Math.round((p.received * 100) / total)) : 0;
        bar.style.width = percent + '%';
        text.textContent = total > 0
          ? `${formatSize(p.received)} из ${formatSize(total)} · ${percent}%`
          : formatSize(p.received);
      } else if (p.phase === 'extract') {
        bar.style.width = '100%';
        title.textContent = 'Распаковываем обновление…';
        text.textContent = 'Проверяем файлы';
      } else if (p.phase === 'install') {
        bar.style.width = '100%';
        title.textContent = 'Устанавливаем обновление…';
        text.textContent = 'Приложение закроется и запустится снова';
      }
    } catch { /* сервер уже перезапускается */ }
  }, 350);

  try {
    await api.applyUpdate();
  } catch (err) {
    finished = true;
    clearInterval(progressTimer);
    overlay.remove();
    toast('Обновление не установлено: ' + err.message, 'err');
    return;
  }

  finished = true;
  clearInterval(progressTimer);
  bar.style.width = '100%';
  title.textContent = 'Обновление устанавливается…';
  text.textContent = 'Приложение закроется, файл будет заменён и Kanban запустится снова.';

  // Ждём, пока сервер поднимется после перезапуска, и перезагружаем вкладку.
  const started = Date.now();
  const timer = setInterval(async () => {
    if (Date.now() - started > 90000) {
      clearInterval(timer);
      overlay.querySelector('.overlay-box').innerHTML =
        '<h2>Не удалось дождаться перезапуска</h2><p>Запустите Kanban.exe вручную.</p>';
      return;
    }
    try {
      const res = await fetch('/api/health', { cache: 'no-store' });
      if (res.ok) {
        const data = await res.json();
        if (data.version === updateInfo.version) {
          clearInterval(timer);
          location.reload();
        }
      }
    } catch { /* приложение ещё перезапускается */ }
  }, 1000);
}

/* ============================ синхронизация (WebDAV) ============================ */

let syncStatus = null;
let syncBusy = false;

/** В сборке без синхронизации прячем все её элементы и не опрашиваем сервер. */
async function detectFeatures() {
  try {
    const health = await request('GET', '/api/health');
    syncSupported = health.sync !== false;
  } catch {
    syncSupported = false;
  }

  if (syncSupported) return;

  for (const sel of ['#btnSyncNow', '#syncState', '#syncBanner', '#syncDialog']) {
    const node = $(sel);
    if (node) node.classList.add('hidden');
  }
}

async function refreshSyncStatus({ silent = true } = {}) {
  if (!syncSupported) return null;
  try {
    syncStatus = await api.syncStatus();
    renderSyncStatus();
    return syncStatus;
  } catch (err) {
    if (!silent) toast('Не удалось получить состояние синхронизации: ' + err.message, 'err');
    return null;
  }
}

function renderSyncStatus() {
  const s = syncStatus;
  const el = $('#syncState');
  el.classList.remove('err', 'warn');

  if (!s || !s.configured) {
    el.textContent = '';
    el.title = '';
  } else if (s.running) {
    el.textContent = '⇅ синхронизация…';
    el.title = 'Синхронизация выполняется';
  } else if (s.conflict) {
    el.textContent = '⇅ конфликт';
    el.title = 'Данные менялись на двух ПК — выберите версию в баннере';
    el.classList.add('warn');
  } else if (s.lastError) {
    el.textContent = '⇅ ошибка';
    el.title = s.lastError;
    el.classList.add('err');
  } else {
    const time = (s.lastSyncAt || '').slice(11, 16);
    el.textContent = time ? `⇅ ${time}` : '⇅ не синхронизировано';
    el.title = s.lastMessage || (s.pendingChanges ? 'Есть несинхронизированные изменения' : 'Синхронизировано');
    el.classList.toggle('err', !!s.pendingChanges && !s.lastSyncAt);
  }

  renderSyncBanner();
  updateSyncDialogStatus();
}

function renderSyncBanner() {
  const banner = $('#syncBanner');
  if (!syncStatus?.conflict) {
    banner.classList.add('hidden');
    return;
  }

  const parts = ['Данные менялись и на этом ПК, и на сервере.'];
  if (syncStatus.lastSyncAt) parts.push('Последняя синхронизация: ' + syncStatus.lastSyncAt + '.');
  $('#syncBannerText').textContent = parts.join(' ') +
    ' Выберите, какую версию оставить — вторая сохранится в backups.';
  banner.classList.remove('hidden');
}

async function resolveSync(mode) {
  if (syncBusy) return;
  const question = mode === 'pull'
    ? 'Взять версию с сервера? Текущие изменения на этом ПК будут сохранены в backups и заменены.'
    : mode === 'both'
      ? 'Взять версию с сервера, а свою сохранить в backups?'
      : 'Отправить версию с этого ПК на сервер? Версия на сервере будет заменена.';
  if (!confirm(question)) return;

  await runSync(mode, { resolve: true });
}

async function runSync(mode = 'auto', { resolve = false } = {}) {
  if (!syncSupported) return;
  if (syncBusy) return;

  syncBusy = true;
  syncStatus = { ...(syncStatus || {}), running: true };
  renderSyncStatus();

  try {
    const result = resolve ? await api.syncResolve(mode) : await api.syncRun(mode);
    showSyncLog(syncSummary(result));

    if (result.conflict) {
      toast('Конфликт: данные менялись на двух ПК — выберите версию', 'err');
    } else if (!result.ok) {
      toast(result.message || result.error || 'Синхронизация не выполнена', 'err');
    } else {
      toast(result.message || 'Синхронизация выполнена', 'ok');
    }

    if (result.action === 'pull' || result.action === 'both') await refresh();
  } catch (err) {
    showSyncLog('Ошибка: ' + err.message);
    toast('Синхронизация: ' + err.message, 'err');
  } finally {
    syncBusy = false;
    await refreshSyncStatus();
  }
}

function syncSummary(result) {
  if (!result) return '';
  const lines = [];
  const files = (n) => n ? ` (вложений: ${n})` : '';

  switch (result.action) {
    case 'push': lines.push('Отправлено на сервер' + files(result.uploadedFiles)); break;
    case 'pull': lines.push('Получено с сервера' + files(result.downloadedFiles)); break;
    case 'both': lines.push('Взята версия с сервера, ваша сохранена в backups'); break;
    case 'conflict': lines.push('Конфликт: данные менялись на этом ПК и на сервере'); break;
    case 'none': lines.push('Изменений нет — данные уже совпадают'); break;
    case 'test': lines.push(result.message || 'Подключение работает'); break;
    case 'busy': lines.push('Синхронизация уже выполняется'); break;
    default:
      lines.push(result.message || result.error || 'Готово');
  }

  if (result.error && result.action !== 'error') lines.push('Ошибка: ' + result.error);
  if (result.remoteDevice) lines.push('Версия на сервере: ' + result.remoteDevice);
  if (result.at) lines.push('Время: ' + new Date(result.at).toLocaleTimeString('ru-RU'));
  return lines.join('\n');
}

function showSyncLog(text) {
  const log = $('#syncLog');
  log.textContent = text || '';
  log.classList.toggle('hidden', !text);
}

async function openSyncDialog() {
  if (!syncSupported) return;
  const dlg = $('#syncDialog');
  const s = await refreshSyncStatus({ silent: false });

  if (s) {
    $('#syncUrl').value = s.url || '';
    $('#syncLogin').value = s.login || '';
    $('#syncFolder').value = s.folder || 'Kanban';
    $('#syncEnabled').checked = !!s.enabled;
    $('#syncOnStart').checked = !!s.onStart;
    $('#syncOnExit').checked = !!s.onExit;
    $('#syncPassword').value = '';
    $('#syncPassword').placeholder = s.hasPassword ? 'сохранён — оставьте пустым' : 'пароль приложения';
  }

  showSyncLog('');
  updateSyncDialogStatus();
  dlg.showModal();
}

function updateSyncDialogStatus() {
  const line = $('#syncStatusLine');
  if (!line) return;

  const s = syncStatus;
  if (!s) {
    line.textContent = 'Состояние недоступно.';
  } else if (s.running) {
    line.textContent = 'Синхронизация выполняется…';
  } else if (!s.configured) {
    line.textContent = 'Синхронизация не настроена: заполните адрес, логин и пароль приложения.';
  } else if (s.lastError) {
    line.textContent = 'Ошибка: ' + s.lastError;
  } else {
    const pending = s.pendingChanges ? ' Есть несинхронизированные изменения.' : '';
    line.textContent = 'Последняя синхронизация: ' + (s.lastSyncAt || 'ещё не выполнялась') + '.' + pending;
  }
}

async function saveSyncFields() {
  const payload = {
    url: $('#syncUrl').value.trim(),
    login: $('#syncLogin').value.trim(),
    folder: $('#syncFolder').value.trim() || 'Kanban',
    enabled: $('#syncEnabled').checked,
    onStart: $('#syncOnStart').checked,
    onExit: $('#syncOnExit').checked,
  };

  const password = $('#syncPassword').value;
  if (password) payload.password = password;

  try {
    syncStatus = await api.syncSave(payload);
    $('#syncPassword').value = '';
    $('#syncPassword').placeholder = syncStatus.hasPassword ? 'сохранён — оставьте пустым' : 'пароль приложения';
    renderSyncStatus();
    return true;
  } catch (err) {
    toast('Не удалось сохранить настройки: ' + err.message, 'err');
    return false;
  }
}

async function saveSyncSettings(e) {
  e.preventDefault();
  if (!(await saveSyncFields())) return;
  toast('Настройки синхронизации сохранены', 'ok');
  $('#syncDialog').close();
}

async function testSync() {
  const btn = $('#btnSyncTest');
  if (!(await saveSyncFields())) return;

  btn.disabled = true;
  showSyncLog('Проверяем подключение…');
  try {
    const result = await api.syncTest();
    showSyncLog(syncSummary(result));
    toast(result.message || 'Подключение работает', 'ok');
  } catch (err) {
    showSyncLog('Ошибка: ' + err.message);
    toast('Не удалось подключиться: ' + err.message, 'err');
  } finally {
    btn.disabled = false;
  }
}

async function runSyncFromDialog() {
  if (!(await saveSyncFields())) return;
  $('#syncDialog').close();
  await runSync('auto');
}

/** После запуска приложение может синхронизироваться в фоне — дождёмся результата. */
async function pollSyncAfterStart() {
  if (!syncSupported) return;
  const status = await refreshSyncStatus();
  if (!status?.running) {
    if (status && (status.lastAction === 'pull' || status.lastAction === 'both')) await refresh();
    return;
  }

  let attempts = 0;
  const timer = setInterval(async () => {
    attempts++;
    const s = await refreshSyncStatus();
    if (!s || !s.running || attempts > 60) {
      clearInterval(timer);
      if (s && (s.lastAction === 'pull' || s.lastAction === 'both')) await refresh();
    }
  }, 1500);
}

/* ============================ тосты ============================ */

function toast(message, kind = '') {
  const box = $('#toasts');
  const node = document.createElement('div');
  node.className = 'toast' + (kind ? ' ' + kind : '');
  node.textContent = message;
  box.appendChild(node);
  setTimeout(() => {
    node.style.opacity = '0';
    node.style.transition = 'opacity .25s';
    setTimeout(() => node.remove(), 260);
  }, kind === 'err' ? 6000 : 3200);
}

/* ============================ тема ============================ */

function applyTheme(theme) {
  document.body.dataset.theme = theme;
  localStorage.setItem('kanban.theme', theme);
  $('#btnTheme').title = theme === 'light' ? 'Тёмная тема' : 'Светлая тема';
}

/* ============================ масштаб интерфейса ============================ */

const FONT_MIN = 0.8;
const FONT_MAX = 1.5;
const FONT_STEP = 0.1;
let fontScale = 1;

function applyFontScale(value, notify = false) {
  let scale = Number(value);
  if (!Number.isFinite(scale)) scale = 1;

  scale = Math.min(FONT_MAX, Math.max(FONT_MIN, Math.round(scale * 10) / 10));
  fontScale = scale;

  // zoom на <html> масштабирует всю страницу целиком, как встроенный зум браузера.
  document.documentElement.style.zoom = scale === 1 ? '' : String(scale);
  localStorage.setItem('kanban.fontScale', String(scale));

  const label = Math.round(scale * 100) + '%';
  const node = $('#fontScale');
  if (node) node.textContent = label;

  const minus = $('#btnFontMinus');
  const plus = $('#btnFontPlus');
  if (minus) minus.disabled = scale <= FONT_MIN + 0.001;
  if (plus) plus.disabled = scale >= FONT_MAX - 0.001;

  document.body.classList.toggle('font-large', scale >= 1.2);

  if (notify) toast('Масштаб интерфейса: ' + label);
}

function changeFontScale(delta) {
  applyFontScale(fontScale + delta, true);
}

function resetFontScale() {
  applyFontScale(1, true);
}

/* ============================ инициализация ============================ */

function bindUi() {
  $('#search').addEventListener('input', (e) => {
    query = e.target.value.trim();
    renderBoard();
    renderStatus();
  });

  $('#btnAddColumn').onclick = () => {
    const board = currentBoard();
    if (board) addColumn(board.id);
  };

  $('#btnNewBoard').onclick = createBoard;

  $('#btnMenu').onclick = (e) => {
    e.stopPropagation();
    mainMenu(e.currentTarget);
  };

  $('#btnTheme').onclick = () => {
    applyTheme(document.body.dataset.theme === 'light' ? 'dark' : 'light');
  };

  $('#btnFontMinus').onclick = () => changeFontScale(-FONT_STEP);
  $('#btnFontPlus').onclick = () => changeFontScale(FONT_STEP);

  $('#btnFilters').onclick = (e) => {
    e.stopPropagation();
    const panel = $('#filterPanel');
    const open = panel.classList.toggle('hidden') === false;
    e.currentTarget.setAttribute('aria-expanded', String(open));
  };

  $('#btnClearFilters').onclick = clearFilters;
  $('#btnArchive').onclick = openArchive;
  $('#btnBackup').onclick = backupNow;
  $('#btnFolder').onclick = () => request('POST', '/api/reveal', {}).catch(() => {});
  $('#btnSyncNow').onclick = () => runSync('auto');
  $('#btnSyncPull').onclick = () => resolveSync('pull');
  $('#btnSyncPush').onclick = () => resolveSync('push');
  $('#btnSyncLater').onclick = () => $('#syncBanner').classList.add('hidden');
  $('#btnSyncTest').onclick = testSync;
  $('#btnSyncRun').onclick = runSyncFromDialog;
  $('#syncForm').addEventListener('submit', saveSyncSettings);
  setupTagSuggest();

  $('#cardForm').addEventListener('submit', saveCard);
  $('#btnDeleteCard').onclick = deleteEditingCard;
  $('#btnArchiveCard').onclick = archiveEditingCard;
  $('#btnAddCheck').onclick = addChecklistItem;
  $('#btnAddLink').onclick = addLink;
  $('#btnAddFile').onclick = () => $('#fileInput').click();
  $('#fileInput').onchange = async (e) => {
    await addFiles(e.target.files);
    e.target.value = '';
  };
  $('#cardDialog').addEventListener('close', () => {
    editingCardId = null;
    editingColumn = null;
  });

  $('#btnUpdateApply').onclick = applyUpdate;
  $('#btnUpdateClose').onclick = () => {
    dismissedUpdate = updateInfo?.version ?? null;
    $('#updateBanner').classList.add('hidden');
  };

  for (const node of $$('[data-close]')) {
    node.onclick = () => node.closest('dialog')?.close();
  }

  $('#cardDialog').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      $('#cardForm').requestSubmit();
    }
  });

  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
      e.preventDefault();
      $('#search').focus();
      $('#search').select();
    }

    if (e.ctrlKey || e.metaKey) {
      const plus = e.key === '+' || e.key === '=' || e.code === 'NumpadAdd';
      const minus = e.key === '-' || e.key === '_' || e.code === 'NumpadSubtract';
      if (plus) { e.preventDefault(); changeFontScale(FONT_STEP); }
      else if (minus) { e.preventDefault(); changeFontScale(-FONT_STEP); }
      else if (e.key === '0' || e.code === 'Numpad0') { e.preventDefault(); resetFontScale(); }
    }

    if (e.key === 'Escape') hideMenu();
  });

  document.addEventListener('mousedown', (e) => {
    const menu = $('#menu');
    if (!menu.classList.contains('hidden') && !menu.contains(e.target)) hideMenu();
  });

  window.addEventListener('beforeunload', (e) => {
    if (dragCardId != null) e.preventDefault();
  });
}

async function boot() {
  applyTheme(localStorage.getItem('kanban.theme') || 'dark');
  applyFontScale(localStorage.getItem('kanban.fontScale') || 1);
  loadFilters();
  activeBoardId = Number(localStorage.getItem('kanban.board')) || null;
  bindUi();
  await detectFeatures();
  await refresh();
  checkUpdates(false);
  setInterval(() => checkUpdates(false), 60000);
  pollSyncAfterStart();
}

boot();
