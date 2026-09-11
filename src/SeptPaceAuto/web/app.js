'use strict';
/* Interface de l’application desktop. Aucune donnée locale : tout vient de l’hôte WebView2
   par un pont JSON-RPC (window.chrome.webview), et tout envoi part réellement vers 7pace. */

/* ---------- pont avec l’hôte ---------- */

const HOST_TIMEOUT = 20000;
const host = (() => {
  const bridge = window.chrome?.webview ?? null;
  const pending = new Map();
  const listeners = new Map();
  let nextId = 1;
  if (bridge) bridge.addEventListener('message', event => {
    let message;
    try { message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; } catch { return; }
    if (!message || typeof message !== 'object') return;
    if (typeof message.event === 'string') {
      for (const handler of listeners.get(message.event) ?? []) handler(message.payload);
      return;
    }
    const waiting = pending.get(message.id);
    if (!waiting) return;
    pending.delete(message.id);
    clearTimeout(waiting.timer);
    if (message.error) waiting.reject(new Error(String(message.error)));
    else waiting.resolve(message.result);
  });
  return {
    available: Boolean(bridge),
    /* Chaque appel porte un identifiant et un délai : une absence de réponse devient une erreur lisible. */
    call(method, params = {}) {
      if (!bridge) return Promise.reject(new Error('Le pont avec l’application n’est pas disponible. Ferme puis relance 7pace auto.'));
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error(`L’application n’a pas répondu en ${HOST_TIMEOUT / 1000} secondes (${method}). Rien n’a été enregistré ni envoyé.`));
        }, HOST_TIMEOUT);
        pending.set(id, { resolve, reject, timer });
        bridge.postMessage(JSON.stringify({ id, method, params }));
      });
    },
    on(event, handler) {
      const handlers = listeners.get(event) ?? [];
      handlers.push(handler);
      listeners.set(event, handlers);
    }
  };
})();

/* ---------- état ---------- */

/* Vocabulaire d’activités : les trois clés structurelles sont figées, les autres viennent
   des réglages (libellé et élément de travail). Aucun numéro n’est écrit ici. */
const BASE_ACTIVITY_NAMES = { ticket: 'Développement', unknown: 'À attribuer', excluded: 'Pause / absence' };
const ACTIVITY_KEYS = ['standup', 'meeting', 'review', 'planning', 'training'];
const DEFAULT_ACTIVITY_LABELS = { standup: 'Stand-up', meeting: 'Réunion', review: 'Revue de sprint', planning: 'Rétro / planning', training: 'Formation' };
/* Libellés d’état courts : la barre les porte en entier, jamais coupés au milieu d’un mot. */
const trackingLabels = { running: 'En cours', paused: 'En pause', 'outside-hours': 'Hors horaires', 'no-repo': 'Dépôt introuvable', 'not-configured': 'Non configuré' };
let activities = ACTIVITY_KEYS.map(key => ({ key, label: DEFAULT_ACTIVITY_LABELS[key], workItem: null }));
let activityNames = { ...BASE_ACTIVITY_NAMES, ...DEFAULT_ACTIVITY_LABELS };
let fixedTasks = {};

/* Axe horaire : 08:00 → 18:00. L’échelle n’est plus figée à 1 px par minute — elle vient
   de la hauteur réellement disponible, divisée par l’amplitude affichée, avec un plancher
   lisible. Elle est publiée en variables CSS (--hour, --minute, --timeline) et tout ce qui
   se pose sur l’axe s’exprime en multiples de --minute : changer l’échelle suffit à tout
   replacer, sans repositionner un seul nœud. */
const DAY_START = 480;
const DAY_END = 1080;
const SPAN = DAY_END - DAY_START;
const HOURS = SPAN / 60;
/* Plancher : une heure ne descend pas sous 64 px, soit 16 px pour un créneau de 15 min —
   il reste lisible et cliquable. En dessous, la surface défile proprement au lieu de se
   comprimer : à 700 px de haut la grille glisse, elle ne s’écrase pas. */
const MIN_HOUR_PX = 64;
/* Journée : une seule colonne, donc la grille peut respirer davantage que la semaine.
   L’échelle prend toute la hauteur offerte — la journée se termine sur le dernier filet
   d’heure, en bas de la surface — et ne descend pas sous 36 px par heure : en dessous,
   la surface défile au lieu de s’écraser. Le plancher de hauteur d’un créneau reste
   celui de la semaine : un créneau d’une minute garde 6 px, visible et cliquable. */
const DAY_HOUR_MIN = 36;
/* Position sur l’axe, exprimée en minutes : la mise à l’échelle reste au CSS. */
const atMinute = value => `calc(var(--minute) * ${value})`;
let LUNCH = [750, 810];
let WORK_WINDOWS = [[510, 750], [810, 1020]];

const days = new Map();
let entries = [];
let selectedDay = null;
let period = 'week';
let editingId = null;
let tracking = { paused: false, branch: null, bug: null, workItem: null, title: '', elapsedSeconds: 0, state: 'running' };
let elapsedSeconds = 0;
let timerHandle = null;
let sending = false;
let confirmingDelete = false;
let loadToken = 0;
/* Réglages et mise à jour : l’hôte reste la référence, l’interface n’invente aucune valeur. */
let configured = true;
/* Configuration guidée déjà faite : l’hôte est la référence, l’absence d’information ne la rejoue pas. */
let onboarded = true;
let settingsState = null;
let appVersion = null;
let checkingUpdate = false;
let applyingUpdate = false;

const $ = selector => document.querySelector(selector);
function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
function button(className, label) {
  const node = element('button', className, label);
  node.type = 'button';
  return node;
}
const minutes = value => { const [hour, minute] = value.split(':').map(Number); return hour * 60 + minute; };
const asTime = value => `${String(Math.floor(value / 60)).padStart(2, '0')}:${String(value % 60).padStart(2, '0')}`;
const asHour = value => value % 60 ? `${Math.floor(value / 60)} h ${String(value % 60).padStart(2, '0')}` : `${Math.floor(value / 60)} h`;
const workWindowsLabel = () => WORK_WINDOWS.map(([open, close]) => `${asHour(open)} et ${asHour(close)}`).join(', ou entre ');
const duration = entry => minutes(entry.end) - minutes(entry.start);
const formatDuration = value => value < 60 ? `${value} min` : `${Math.floor(value / 60)} h${value % 60 ? ` ${String(value % 60).padStart(2, '0')}` : ''}`;
const isUnassigned = entry => entry.activity === 'unknown' || (entry.activity !== 'excluded' && !entry.workItem);
const isSent = entry => Boolean(entry.sentAt);
const dateKey = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
const asDate = key => new Date(`${key}T12:00:00`);
const longDate = date => date.toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
const dayDate = date => date.toLocaleDateString('fr-FR', { weekday: 'long', day: 'numeric', month: 'long' });
const capitalize = text => text.charAt(0).toUpperCase() + text.slice(1);
const startOfMonth = date => new Date(date.getFullYear(), date.getMonth(), 1, 12);
const startOfWeek = date => { const first = new Date(date); first.setDate(first.getDate() - (first.getDay() + 6) % 7); return first; };
const addDays = (date, count) => { const next = new Date(date); next.setDate(next.getDate() + count); return next; };
const entriesOf = key => days.get(key) ?? [];
const chronological = items => [...items].sort((a, b) => minutes(a.start) - minutes(b.start));
const included = list => list.filter(entry => entry.activity !== 'excluded');
const sendable = list => included(list).filter(entry => !isSent(entry));
function dayStats(key) {
  const kept = included(entriesOf(key));
  return {
    count: entriesOf(key).length,
    total: kept.reduce((sum, entry) => sum + duration(entry), 0),
    remaining: kept.filter(entry => !isSent(entry)).filter(isUnassigned).reduce((sum, entry) => sum + duration(entry), 0)
  };
}
function dayAriaLabel(date, action) {
  const key = dateKey(date);
  const { count, total, remaining } = dayStats(key);
  return `${capitalize(longDate(date))}, ${count ? formatDuration(total) : 'aucun créneau'}${remaining ? `, dont ${formatDuration(remaining)} à attribuer` : ''}. ${action}`;
}
function sentLabel(entry) {
  const date = new Date(entry.sentAt);
  if (Number.isNaN(date.getTime())) return String(entry.sentAt);
  return date.toLocaleString('fr-FR', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' });
}

let miniMonth = startOfMonth(new Date());
const form = $('#entry-form');

/* ---------- mouvement et retour immédiat ---------- */

/* Une seule grammaire de mouvement : 160 ms, et rien du tout si le poste demande moins d’animation. */
const PANEL_MOTION = 160;
const BLOCK_LEAVE = 160;
const FEEDBACK_LIFE = 4000;
/* Un appel lent est le seul cas qui montre le squelette : un chargement rapide ne clignote pas. */
const SLOW_CALL = 600;
const lessMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
const reduceMotion = () => lessMotion.matches;

/* Marque posée le temps d’une animation. Un nœud ne porte qu’une marque à la fois :
   deux mouvements simultanés sur la même surface se contrarieraient. */
const burstState = new Map();
function burst(node, name, value, life = PANEL_MOTION) {
  if (!node || reduceMotion()) return;
  const previous = burstState.get(node);
  if (previous) {
    clearTimeout(previous.timer);
    delete node.dataset[previous.name];
  }
  void node.offsetWidth;
  node.dataset[name] = value;
  burstState.set(node, { name, timer: setTimeout(() => { delete node.dataset[name]; burstState.delete(node); }, life) });
}
/* Un texte qui change se croise sur place : l’ancien reste posé au-dessus du nouveau le
   temps de s’effacer. Le texte logique est mémorisé pour ignorer le fantôme encore présent. */
const TEXT_SWAP = 150;
const textGhosts = new WeakMap();
const textOf = node => (textGhosts.has(node) ? textGhosts.get(node).value : node.textContent);
function swapText(node, value, { force = false } = {}) {
  if (!node) return false;
  const text = value === null || value === undefined ? '' : String(value);
  const previous = textOf(node);
  if (previous === text) {
    /* Même phrase rejouée : elle doit se voir arriver de nouveau. */
    if (!force || !text || reduceMotion()) return false;
    node.classList.remove('text-in');
    void node.offsetWidth;
    node.classList.add('text-in');
    return false;
  }
  const state = textGhosts.get(node);
  if (state) {
    clearTimeout(state.timer);
    state.ghost.remove();
    textGhosts.delete(node);
  }
  node.textContent = text;
  if (reduceMotion()) return true;
  node.classList.add('text-host');
  if (previous) {
    /* Le fantôme est hors flux : il recouvre la nouvelle valeur puis s’efface. */
    const ghost = element('span', 'text-ghost', previous);
    ghost.setAttribute('aria-hidden', 'true');
    node.append(ghost);
    textGhosts.set(node, { value: text, ghost, timer: setTimeout(() => { ghost.remove(); textGhosts.delete(node); }, TEXT_SWAP) });
    return true;
  }
  node.classList.remove('text-in');
  void node.offsetWidth;
  node.classList.add('text-in');
  return true;
}
const swapTextAt = (selector, value) => swapText($(selector), value);

/* Une seule mécanique d’apparition : ce qui arrive entre, ce qui part sort avant d’être
   replié. « Ouvert » reste une question de hidden : un nœud replié n’est plus focalisable. */
const panelTimers = new Map();
const isPanelOpen = node => Boolean(node) && !node.hidden && node.dataset.leaving !== 'true';
function reveal(node, shown, { instant = false } = {}) {
  if (!node) return;
  const timer = panelTimers.get(node);
  if (timer) {
    clearTimeout(timer);
    panelTimers.delete(node);
  }
  if (shown) {
    const wasLeaving = node.dataset.leaving === 'true';
    const wasHidden = node.hidden;
    delete node.dataset.leaving;
    node.hidden = false;
    if ((!wasHidden && !wasLeaving) || instant || reduceMotion()) {
      delete node.dataset.entering;
      return;
    }
    node.dataset.entering = 'true';
    panelTimers.set(node, setTimeout(() => { delete node.dataset.entering; panelTimers.delete(node); }, PANEL_MOTION));
    return;
  }
  delete node.dataset.entering;
  if (node.hidden) {
    delete node.dataset.leaving;
    return;
  }
  if (instant || reduceMotion()) {
    delete node.dataset.leaving;
    node.hidden = true;
    return;
  }
  /* Un second repli pendant l’animation de sortie ne doit pas perdre la minuterie :
     sans elle, le nœud resterait affiché — invisible mais présent — et n’importe quelle
     animation de surface le ferait réapparaître par-dessus celle qui arrive. */
  node.dataset.leaving = 'true';
  panelTimers.set(node, setTimeout(() => {
    panelTimers.delete(node);
    delete node.dataset.leaving;
    /* Replié à la fin de l’animation : son contenu n’est plus focalisable. */
    node.hidden = true;
  }, PANEL_MOTION));
}
const revealAt = (selector, shown, options) => reveal($(selector), shown, options);

/* Réconciliation par identifiant : un nœud déjà posé est mis à jour en place, un nouveau
   entre, un disparu sort avant d’être retiré. La surface ne se repeint jamais entre deux relevés. */
function exitNode(node, className = 'leaving') {
  if (!node || node.dataset.exit === 'true') return;
  if (reduceMotion()) {
    node.remove();
    return;
  }
  node.dataset.exit = 'true';
  node.classList.add(className);
  setTimeout(() => node.remove(), PANEL_MOTION);
}
function reconcile(container, items, { create, update, enter = null, ordered = false }) {
  const known = new Map();
  for (const node of container.children) {
    const key = node.dataset.key;
    if (key === undefined || node.dataset.exit === 'true') continue;
    known.set(key, node);
  }
  const placed = [];
  items.forEach((item, index) => {
    const key = String(item.key);
    const existing = known.get(key);
    known.delete(key);
    if (existing) {
      update(existing, item.value, index, false);
      placed.push(existing);
      return;
    }
    const node = create(item.value, index);
    node.dataset.key = key;
    update(node, item.value, index, true);
    container.append(node);
    enter?.(node, index);
    placed.push(node);
  });
  /* Dans une liste, l’ordre du DOM est l’ordre lu et l’ordre du clavier : les nœuds
     réutilisés sont reposés dans l’ordre demandé, de la fin vers le début, sans
     toucher à ceux qui sont en train de sortir. */
  if (ordered) {
    let after = null;
    for (let index = placed.length - 1; index >= 0; index--) {
      const node = placed[index];
      if (node.nextElementSibling !== after) container.insertBefore(node, after);
      after = node;
    }
  }
  for (const node of known.values()) exitNode(node);
}

/* L’attente est portée par la commande qui l’a déclenchée : elle reste focalisable,
   aria-busy la décrit, et aucun voile ne recouvre l’écran. */
const isPending = node => node?.dataset.pending === 'true';
function pending(node, label) {
  if (!node) return () => {};
  const slot = label ? (node.querySelector('span') ?? node) : null;
  const previousLabel = slot ? textOf(slot) : null;
  const previousDisabled = node.getAttribute('aria-disabled');
  node.dataset.pending = 'true';
  node.setAttribute('aria-busy', 'true');
  node.setAttribute('aria-disabled', 'true');
  if (slot) swapText(slot, label);
  let released = false;
  return () => {
    if (released) return;
    released = true;
    delete node.dataset.pending;
    node.removeAttribute('aria-busy');
    if (previousDisabled === null) node.removeAttribute('aria-disabled');
    else node.setAttribute('aria-disabled', previousDisabled);
    if (slot && previousLabel !== null) swapText(slot, previousLabel);
  };
}

/* Réussite : une phrase courte qui s’efface. Échec : la phrase de l’hôte, gardée jusqu’à ce
   qu’elle soit masquée ou remplacée. Le texte des lecteurs d’écran reste porté par #announcement. */
const feedbackTimers = new Map();
function setFeedback(selector, message, kind = 'good') {
  const node = $(selector);
  if (!node) return;
  for (const timer of feedbackTimers.get(selector) ?? []) clearTimeout(timer);
  feedbackTimers.delete(selector);
  const text = typeof message === 'string' ? message : '';
  swapText(node, text, { force: true });
  if (!text) {
    delete node.dataset.kind;
    return;
  }
  node.dataset.kind = kind;
  if (kind === 'error') return;
  /* La réussite s’efface d’elle-même : le retrait est un croisé, pas une disparition. */
  feedbackTimers.set(selector, [setTimeout(() => setFeedback(selector, ''), FEEDBACK_LIFE)]);
}
const clearFeedback = selector => setFeedback(selector, '');

/* Squelette de chargement : la vraie grille, son contenu estompé, jamais une surface vide. */
let slowTimer = null;
let slowDepth = 0;
function showSkeleton(value) {
  if (value) document.body.dataset.loading = 'true';
  else delete document.body.dataset.loading;
  $('.surface-scroll')?.setAttribute('aria-busy', String(Boolean(value)));
}
function watchSlowCall(onSlow) {
  slowDepth += 1;
  if (slowDepth === 1) slowTimer = setTimeout(() => {
    slowTimer = null;
    onSlow?.();
    showSkeleton(true);
  }, SLOW_CALL);
  let released = false;
  return () => {
    if (released) return;
    released = true;
    slowDepth = Math.max(0, slowDepth - 1);
    if (slowDepth) return;
    clearTimeout(slowTimer);
    slowTimer = null;
    showSkeleton(false);
  };
}

/* Micro-interactions des créneaux. L’entrée n’est jouée que si la journée ou la période
   affichée change, ou si la surface passe du vide à du contenu : jamais à chaque relevé. */
let dayChanged = false;
let painted = { day: null, period: null, entries: 0 };
function visibleEntryCount() {
  if (period === 'day') return entriesOf(selectedDay).length;
  const anchor = asDate(selectedDay);
  const first = period === 'month' ? startOfWeek(startOfMonth(anchor)) : startOfWeek(anchor);
  let count = 0;
  for (let offset = 0; offset < (period === 'month' ? 42 : 7); offset++) count += entriesOf(dateKey(addDays(first, offset))).length;
  return count;
}
const blockNodes = id => document.querySelectorAll(`.time-block[data-entry="${id}"]`);
/* Créneau qui vient d’être écrit : une bordure accent qui retombe. */
function highlightBlock(id) {
  for (const node of blockNodes(id)) {
    node.classList.remove('saved');
    void node.offsetWidth;
    node.classList.add('saved');
  }
}
/* Sortie jouée avant le retrait du DOM : le créneau part par la même mécanique que
   les nœuds retirés par la réconciliation. */
function collapseBlock(id) {
  const nodes = [...blockNodes(id)];
  if (!nodes.length || reduceMotion()) return Promise.resolve();
  for (const node of nodes) exitNode(node);
  return new Promise(resolve => setTimeout(resolve, BLOCK_LEAVE));
}
/* Le créneau que le suivi est en train d’écrire respire une fois par minute, jamais plus. */
function beatLiveBlock() {
  for (const node of document.querySelectorAll('.time-block.live')) {
    node.classList.remove('beat');
    void node.offsetWidth;
    node.classList.add('beat');
  }
}
const nowMinutes = () => { const now = new Date(); return now.getHours() * 60 + now.getMinutes(); };
/* Le créneau en cours d’écriture est celui qui couvre la minute présente, suivi actif seulement. */
function isLiveEntry(entry, key) {
  if (tracking.state !== 'running' || isSent(entry) || key !== dateKey(new Date())) return false;
  const now = nowMinutes();
  return minutes(entry.start) <= now && minutes(entry.end) >= now;
}

/* Échelle de l’axe horaire : hauteur disponible ÷ amplitude affichée. Les deux vues
   remplissent la surface et ne diffèrent que par leur plancher — 64 px par heure pour
   la semaine, qui partage sa largeur en sept, 36 px pour la journée, qui n’a qu’une
   colonne. Sous le plancher, la surface défile au lieu de s’écraser. Rien n’est
   repositionné ici : blocs, libellés d’heure, bandes non travaillées et filet de
   l’heure courante sont posés en multiples de --minute, donc écrire la variable
   replace toute la grille, sans mise en page animée. */
let hourPx = 0;
let dayHourPx = 0;
function surfaceInnerHeight() {
  const host = $('.surface-scroll');
  if (!host) return 0;
  const style = getComputedStyle(host);
  return host.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
}
/* La semaine réserve en plus sa ligne d’en-têtes ; la journée, non. Repliée, cette ligne
   ne mesure rien : la dernière mesure connue sert alors de référence, sinon l’échelle
   de chaque vue dépendrait de celle qui est affichée — et la journée resterait courte
   d’une trentaine de pixels pendant le croisé de 160 ms qui suit le changement de vue. */
let weekHeadPx = 0;
function weekHeadHeight() {
  const measured = $('.week-head')?.offsetHeight ?? 0;
  if (measured) weekHeadPx = measured;
  return weekHeadPx;
}
function applyTimelineScale() {
  if (isMini()) return;
  /* Une heure entière en pixels : les filets d’heure et de demi-heure restent nets. */
  const inner = surfaceInnerHeight();
  if (!Number.isFinite(inner) || inner <= 0) return;
  const root = document.documentElement.style;
  const week = Math.max(MIN_HOUR_PX, Math.floor((inner - weekHeadHeight()) / HOURS));
  if (week !== hourPx) {
    hourPx = week;
    root.setProperty('--hour', `${hourPx}px`);
    root.setProperty('--minute', `${hourPx / 60}px`);
    root.setProperty('--timeline', `${hourPx * HOURS}px`);
  }
  const day = Math.max(DAY_HOUR_MIN, Math.floor(inner / HOURS));
  if (day !== dayHourPx) {
    dayHourPx = day;
    root.setProperty('--day-hour', `${dayHourPx}px`);
    root.setProperty('--day-minute', `${dayHourPx / 60}px`);
    root.setProperty('--day-timeline', `${dayHourPx * HOURS}px`);
  }
}

/* Pastille de la bascule de vue : mesurée, puis déplacée et étirée par transformation.
   Aucune largeur animée : la pastille garde 1 px et c’est son échelle qui change. */
function placeViewThumb() {
  const thumb = $('.view-switch-thumb');
  const active = document.querySelector('button[data-period][aria-pressed="true"]');
  if (!thumb || !active || !active.offsetWidth) return;
  thumb.style.setProperty('--thumb-x', `${active.offsetLeft}px`);
  thumb.style.setProperty('--thumb-w', String(active.offsetWidth));
}

/* Filet de sélection de la semaine : un seul nœud, qui glisse d’une colonne à l’autre.
   Il est rattaché à chaque rendu : sa position connue est reposée puis relue, sinon la
   transition repartirait de zéro et le filet sauterait. */
const weekUnderline = element('span', 'week-underline');
weekUnderline.setAttribute('aria-hidden', 'true');
let underlinePlace = { x: 0, w: 0 };
function placeWeekUnderline(head, target) {
  if (!head || !target) { weekUnderline.remove(); return; }
  head.append(weekUnderline);
  const next = { x: target.offsetLeft + 6, w: Math.max(0, target.offsetWidth - 12) };
  const write = place => {
    weekUnderline.style.setProperty('--underline-x', `${place.x}px`);
    weekUnderline.style.setProperty('--underline-w', String(place.w));
  };
  write(underlinePlace.w ? underlinePlace : next);
  void weekUnderline.offsetWidth;
  write(next);
  underlinePlace = next;
}

/* Heure courante : un filet unique posé sur la colonne du jour — celle de la semaine,
   ou la colonne de temps de la journée — qui glisse au fil des relevés. Dans la vue
   jour, il porte en plus l’heure écrite dans l’axe. */
const nowLine = element('div', 'now-line');
nowLine.setAttribute('aria-hidden', 'true');
nowLine.append(element('span', 'now-time'));
let nowLinePlace = null;
const NOW_LINE_REFRESH = 30000;
function placeNowLine(container) {
  const value = nowMinutes();
  if (!container || value < DAY_START || value > DAY_END) {
    nowLine.remove();
    return;
  }
  const top = value - DAY_START;
  container.append(nowLine);
  nowLine.title = `Il est ${asTime(value)}`;
  swapText(nowLine.querySelector('.now-time'), asTime(value));
  nowLine.style.transform = `translateY(${atMinute(nowLinePlace ?? top)})`;
  void nowLine.offsetWidth;
  nowLine.style.transform = `translateY(${atMinute(top)})`;
  nowLinePlace = top;
}
function refreshNowLine() {
  if (isMini()) return;
  const today = dateKey(new Date());
  if (period === 'week') { placeNowLine(document.querySelector(`.week-column[data-date="${today}"]`)); return; }
  /* Le filet avance seul : aucune des deux surfaces n’est redessinée pour autant. */
  placeNowLine(period === 'day' && selectedDay === today && planningTarget() === 'day' ? $('.day-grid') : null);
}
/* Divulgation accessible : un bouton porte aria-expanded, un panneau porte hidden.
   <details>/<summary> n’est pas activable par l’automatisation Windows (UIA). */
function disclosure(triggerSelector, panelSelector) {
  const trigger = $(triggerSelector);
  const panel = $(panelSelector);
  const drawer = {
    trigger,
    get open() { return trigger.getAttribute('aria-expanded') === 'true'; },
    set open(value) {
      const next = Boolean(value);
      /* aria-expanded reste synchrone : c’est lui que lit l’automatisation Windows. */
      trigger.setAttribute('aria-expanded', String(next));
      if (next) reveal(panel, true);
      else reveal(panel, false);
    },
    focus() { trigger.focus(); }
  };
  trigger.addEventListener('click', () => { drawer.open = !drawer.open; });
  return drawer;
}
const calendarDisclosure = disclosure('#calendar-summary', '#calendar-panel');
/* Le calendrier surgit au-dessus du planning : un clic ailleurs le referme, comme
   n’importe quel menu de la barre. Échap reste le second chemin. */
document.addEventListener('pointerdown', event => {
  if (!calendarDisclosure.open) return;
  const node = event.target;
  if (node instanceof Element && node.closest('#calendar-disclosure')) return;
  calendarDisclosure.open = false;
});
const statusDetails = disclosure('#status-summary', '#status-popover');
const isMini = () => document.body.dataset.view === 'mini';
function announce(text) { $('#announcement').textContent = text; }
/* Consigne qui attend une action de l’utilisateur : le tiroir d’état s’ouvre pour qu’elle reste lisible. */
function announceAction(text) {
  announce(text);
  if (!isMini() && !isPanelOpen($('#editor')) && !isPanelOpen($('#settings')) && !isPanelOpen($('#preview'))) statusDetails.open = true;
}

/* ---------- erreurs réelles ---------- */

function hostErrorMessage(error) {
  const message = typeof error?.message === 'string' ? error.message.trim() : '';
  return message || 'L’application a renvoyé une erreur sans message.';
}
/* Aucun échec silencieux : le message reste affiché dans le tiroir d’état. */
function reportHostError(error) {
  const message = hostErrorMessage(error);
  swapTextAt('#host-error', message);
  reveal($('#dismiss-host-error'), true);
  announce(message);
  if (isMini()) swapTextAt('#tracking-state', 'Erreur — ouvrir la vue complète');
  else statusDetails.open = true;
}
function clearHostError() {
  swapTextAt('#host-error', '');
  reveal($('#dismiss-host-error'), false);
}

/* ---------- chargement des journées ---------- */

function rangeBounds() {
  const anchor = asDate(selectedDay);
  const weekFirst = startOfWeek(anchor);
  const monthFirst = startOfWeek(startOfMonth(anchor));
  const miniFirst = startOfWeek(miniMonth);
  const bounds = [anchor, weekFirst, addDays(weekFirst, 6), monthFirst, addDays(monthFirst, 41), miniFirst, addDays(miniFirst, 41)];
  return [dateKey(new Date(Math.min(...bounds))), dateKey(new Date(Math.max(...bounds)))];
}
/* La réponse ne contient que les dates peuplées : les autres dates de l’intervalle sont vidées. */
function applyRange(from, to, payload) {
  for (let date = asDate(from), last = asDate(to); date <= last; date = addDays(date, 1)) {
    const key = dateKey(date);
    const list = payload?.[key];
    if (Array.isArray(list) && list.length) days.set(key, list);
    else days.delete(key);
  }
  entries = entriesOf(selectedDay);
}
async function loadVisible({ trigger = null } = {}) {
  if (isMini() || !selectedDay) return;
  const [from, to] = rangeBounds();
  const token = ++loadToken;
  /* La commande qui a demandé la plage porte l’attente ; la grille ne s’estompe que si c’est long. */
  const done = trigger ? pending(trigger) : () => {};
  const settled = watchSlowCall();
  try {
    const result = await host.call('loadRange', { from, to });
    if (token !== loadToken) return;
    applyRange(from, to, result?.days);
    clearHostError();
    render({ keepPreview: true });
  } catch (error) {
    reportHostError(error);
  } finally {
    settled();
    done();
  }
}
function applyDay(payload) {
  if (!payload || typeof payload.date !== 'string') return;
  const list = Array.isArray(payload.entries) ? payload.entries : [];
  if (list.length) days.set(payload.date, list);
  else days.delete(payload.date);
  if (payload.date === selectedDay) entries = entriesOf(selectedDay);
}

/* ---------- navigation ---------- */

function selectDay(key, view = period, { keepEditor = false, trigger = null } = {}) {
  if (!keepEditor && discardEditor()) announce('Édition abandonnée : la journée affichée a changé. Rien n’a été enregistré.');
  selectedDay = key;
  period = view;
  entries = entriesOf(key);
  miniMonth = startOfMonth(asDate(key));
  render();
  loadVisible({ trigger });
}
function openDay(key) {
  /* La journée choisie prend la place du planning : le calendrier surgissant se referme. */
  calendarDisclosure.open = false;
  selectDay(key, 'day');
  const label = $('#period-label');
  label.setAttribute('tabindex', '-1');
  label.focus();
  announce(`Journée du ${dayDate(asDate(key))} affichée.`);
}
function openBlock(entry, key) {
  if (key !== selectedDay) selectDay(key, period, { keepEditor: true });
  openEditor(entry);
  if (period !== 'day') announce(`Créneau du ${dayDate(asDate(key))} ouvert dans le panneau d’édition. La vue ${period === 'week' ? 'semaine' : 'mois'} reste affichée.`);
}
/* Le premier créneau à attribuer : la même intention sert le pied de page et le rail
   de la journée, donc elle n’est écrite qu’une fois. */
function openFirstUnassigned() {
  const unresolved = chronological(sendable(entries)).find(isUnassigned);
  if (!unresolved) return;
  openEditor(unresolved);
  announce(`Attribue le créneau de ${unresolved.start} à ${unresolved.end} avant de prévisualiser l’envoi.`);
}

/* ---------- planning ---------- */

/* L’axe horaire n’est posé qu’une fois : ses étiquettes ne changent jamais. */
function addTimeLabels(container) {
  if (container.querySelector('.time-label')) return;
  for (let value = DAY_START; value < DAY_END; value += 60) {
    const label = element('span', 'time-label', `${value / 60} h`);
    label.style.top = atMinute(value - DAY_START);
    container.append(label);
  }
}
function blockTitle(entry) {
  if (entry.activity === 'unknown') return 'À attribuer';
  return entry.title || activityLabel(entry.activity);
}
function blockDetail(entry) {
  if (entry.activity === 'unknown') return 'Attribuer l’activité';
  if (entry.activity === 'excluded') return 'Exclu de l’envoi';
  if (!entry.workItem) return 'Attribuer un numéro';
  if (entry.activity === 'ticket') return `Élément #${entry.workItem}`;
  return `${activityLabel(entry.activity)} · #${entry.workItem}`;
}
/* Le détail n’apparaît sur le bloc que s’il appelle une action ; sinon il reste dans l’info-bulle. */
const detailIsActionable = entry => isUnassigned(entry) || entry.activity === 'excluded';
/* Le créneau visé par un clic est relu au moment du clic : le nœud survit aux relevés,
   sa donnée non. */
const blockData = new WeakMap();
function createBlock({ sent }) {
  /* Un créneau déjà envoyé n’est plus une commande : ni clic, ni édition, ni renvoi. */
  const block = sent ? element('div', 'time-block sent') : button('time-block');
  block.append(element('span', 'block-time'), element('span', 'block-title'));
  const detail = element('span', sent ? 'block-sent' : 'block-detail');
  detail.hidden = true;
  block.append(detail);
  if (sent) block.setAttribute('role', 'note');
  else block.addEventListener('click', () => {
    const data = blockData.get(block);
    if (data) openBlock(data.entry, data.key);
  });
  return block;
}
function updateBlock(block, { entry, key, sent }, index, created) {
  blockData.set(block, { entry, key });
  block.dataset.entry = String(entry.id);
  const top = Math.min(SPAN, Math.max(0, minutes(entry.start) - DAY_START));
  const bottom = Math.min(SPAN, Math.max(0, minutes(entry.end) - DAY_START));
  const unassigned = isUnassigned(entry);
  const live = isLiveEntry(entry, key);
  block.classList.toggle('unknown', unassigned);
  block.classList.toggle('excluded', entry.activity === 'excluded');
  block.classList.toggle('live', live);
  block.style.top = atMinute(top);
  /* Un créneau très court garde une hauteur touchable, quelle que soit l’échelle. */
  block.style.height = `max(6px, ${atMinute(bottom - top)})`;
  swapText(block.querySelector('.block-time'), `${entry.start} – ${entry.end}`);
  swapText(block.querySelector('.block-title'), blockTitle(entry));
  const detail = block.querySelector(sent ? '.block-sent' : '.block-detail');
  const shown = sent || detailIsActionable(entry);
  if (shown) swapText(detail, sent ? 'Envoyé' : blockDetail(entry));
  reveal(detail, shown, { instant: created });
  if (sent) {
    block.title = `${entry.start} – ${entry.end} · ${blockTitle(entry)} · ${blockDetail(entry)} · envoyé dans 7pace le ${sentLabel(entry)} · non modifiable`;
    block.setAttribute('aria-label', `Créneau de ${entry.start} à ${entry.end}, ${formatDuration(duration(entry))}, ${blockTitle(entry)}, ${blockDetail(entry)}, envoyé dans 7pace le ${sentLabel(entry)}, non modifiable, ${dayDate(asDate(key))}.`);
    return;
  }
  /* Le suivi en cours a son équivalent textuel : le bord accent n’est pas la seule information. */
  block.title = `${entry.start} – ${entry.end} · ${blockTitle(entry)} · ${blockDetail(entry)}${live ? ' · en cours d’écriture par le suivi' : ''}`;
  block.setAttribute('aria-label', `${unassigned ? 'Attribuer' : 'Modifier'} le créneau de ${entry.start} à ${entry.end}, ${formatDuration(duration(entry))}, ${blockTitle(entry)}, ${blockDetail(entry)}${live ? ', en cours d’écriture par le suivi' : ''}, ${dayDate(asDate(key))}.`);
}
/* Un créneau qui arrive entre toujours ; il n’est décalé de 25 ms que si la journée entière
   se repose, plafonné à 150 ms : la journée se pose, elle ne défile pas. */
function enterBlock(node, index) {
  if (reduceMotion()) return;
  node.style.setProperty('--enter-delay', dayChanged ? `${Math.min(index * 25, 150)}ms` : '0ms');
  node.classList.add('enter');
  setTimeout(() => node.classList.remove('enter'), 400);
}
/* L’état « envoyé » change la nature du nœud : il entre en place de l’ancien, il ne le mute pas. */
const blockItems = (list, key) => chronological(list).map(entry => ({
  key: `${entry.id}:${isSent(entry) ? 'envoye' : 'ouvert'}`,
  value: { entry, key, sent: isSent(entry) }
}));
const renderBlocks = (container, list, key) => reconcile(container, blockItems(list, key), {
  create: createBlock,
  update: updateBlock,
  enter: enterBlock
});

/* ---------- journée : deux volets ---------- */

/* Une journée tient en quelques créneaux : étalée sur toute la largeur, une grille
   proportionnelle passe l’essentiel de la fenêtre à dessiner des heures vides. La
   journée est donc un écran à deux volets — une colonne de temps réelle mais étroite,
   à l’échelle compressée, et la largeur ainsi rendue porte le rail de synthèse.
   La colonne garde l’axe, les blocs et le filet de l’heure courante de la semaine :
   seule l’échelle change, et elle est portée par --day-hour. */
const dayOpen = () => WORK_WINDOWS[0]?.[0] ?? DAY_START;
const dayClose = () => WORK_WINDOWS[WORK_WINDOWS.length - 1]?.[1] ?? DAY_END;
const dayTarget = () => WORK_WINDOWS.reduce((sum, [open, close]) => sum + (close - open), 0);

/* Un créneau compte pour un élément de travail, ou pour le temps qui reste à attribuer :
   la colonne et le rail se lient par cette seule clé. */
const groupKey = entry => (entry.activity === 'excluded' ? 'exclu' : isUnassigned(entry) ? 'attribuer' : `item:${entry.workItem}`);

/* Lien colonne ↔ rail : survoler l’un met l’autre en évidence. La marque est reposée
   à chaque rendu plutôt qu’effacée, sinon un relevé casserait le survol en cours. */
let linkedGroup = null;
const isLinked = group => linkedGroup !== null && group === linkedGroup;
function linkGroup(group) {
  const next = group ?? null;
  if (linkedGroup === next) return;
  linkedGroup = next;
  for (const node of document.querySelectorAll('.day-block, .rail-row')) node.classList.toggle('linked', isLinked(node.dataset.group));
}
function bindLink(node) {
  node.addEventListener('pointerenter', () => linkGroup(node.dataset.group));
  node.addEventListener('pointerleave', () => linkGroup(null));
}

/* Périodes non travaillées : avant l’embauche, entre deux fenêtres — le déjeuner par
   construction — et après la débauche. Un fond plus sombre, et un libellé pour le seul
   déjeuner : le reste se lit sans qu’on le nomme. */
function offBands() {
  const bands = [];
  const push = (start, end, label = '') => {
    const from = Math.max(DAY_START, start), to = Math.min(DAY_END, end);
    if (to > from) bands.push({ key: `off:${from}:${to}`, value: { from, to, label } });
  };
  push(DAY_START, dayOpen());
  WORK_WINDOWS.forEach(([, close], position) => {
    const next = WORK_WINDOWS[position + 1];
    if (next) push(close, next[0], 'Déjeuner');
  });
  push(dayClose(), DAY_END);
  return bands;
}
function createBand() {
  const band = element('div', 'day-off');
  band.append(element('span', 'day-off-label'));
  return band;
}
function updateBand(band, { from, to, label }) {
  band.style.top = atMinute(from - DAY_START);
  band.style.height = atMinute(to - from);
  swapText(band.querySelector('.day-off-label'), label);
}

/* Le bloc de la journée est celui de la semaine, avec sa pastille de durée : le titre
   passe en tête, la plage suit, la durée se pose en bas à droite. */
function createDayBlock(value) {
  const block = createBlock(value);
  block.classList.add('day-block');
  block.append(element('span', 'block-chip'));
  bindLink(block);
  block.addEventListener('focus', () => linkGroup(block.dataset.group));
  block.addEventListener('blur', () => linkGroup(null));
  return block;
}
function updateDayBlock(block, value, index, created) {
  updateBlock(block, value, index, created);
  const group = groupKey(value.entry);
  block.dataset.group = group;
  block.classList.toggle('linked', isLinked(group));
  swapText(block.querySelector('.block-chip'), formatDuration(duration(value.entry)));
}

/* Répartition du jour : une ligne par élément de travail, la plus longue en tête, et
   le temps encore à attribuer regroupé en fin de liste. */
function railRows(list) {
  const groups = new Map();
  for (const entry of included(list)) {
    const key = groupKey(entry);
    const row = groups.get(key) ?? { key, unresolved: key === 'attribuer', label: 'À attribuer', value: 0, longest: -1 };
    const length = duration(entry);
    row.value += length;
    /* Un élément porte le libellé de son créneau le plus long : c’est celui qui le décrit. */
    if (!row.unresolved && length > row.longest) {
      row.longest = length;
      row.label = `${blockTitle(entry)} · #${entry.workItem}`;
    }
    groups.set(key, row);
  }
  const rows = [...groups.values()];
  const top = rows.reduce((most, row) => Math.max(most, row.value), 0);
  return [
    ...rows.filter(row => !row.unresolved).sort((a, b) => b.value - a.value),
    ...rows.filter(row => row.unresolved)
  ].map(row => ({ key: row.key, value: { ...row, share: top ? row.value / top : 0 } }));
}
/* La ligne sélectionnée est celle du créneau ouvert dans l’inspecteur : elle est écrite
   ici seulement, et rejouée à l’ouverture comme à la fermeture du panneau. */
function syncRailSelection() {
  const rows = document.querySelectorAll('.rail-row');
  if (!rows.length) return;
  const edited = entries.find(entry => entry.id === editingId);
  const selected = edited ? groupKey(edited) : null;
  for (const row of rows) row.classList.toggle('selected', selected !== null && row.dataset.group === selected);
}
function createRailRow() {
  const row = element('li', 'rail-row');
  const head = element('div', 'rail-row-head');
  head.append(element('span', 'rail-row-label'), element('span', 'rail-row-value'));
  const bar = element('span', 'rail-row-bar');
  bar.setAttribute('aria-hidden', 'true');
  bar.append(element('span', 'rail-row-fill'));
  row.append(head, bar);
  bindLink(row);
  return row;
}
function updateRailRow(row, { key, label, value, share, unresolved }) {
  row.dataset.group = key;
  row.classList.toggle('unresolved', unresolved);
  row.classList.toggle('linked', isLinked(key));
  swapText(row.querySelector('.rail-row-label'), label);
  swapText(row.querySelector('.rail-row-value'), formatDuration(value));
  row.querySelector('.rail-row-fill').style.setProperty('--share', Math.max(0.02, share).toFixed(3));
  row.title = `${label} · ${formatDuration(value)}`;
}

/* Les deux volets sont posés une fois : changer de journée n’en change que le contenu. */
function dayScaffold() {
  const view = $('#entries-list');
  if (view.dataset.ready === 'true') return view;

  const timeline = element('section', 'day-timeline');
  timeline.setAttribute('aria-label', 'Journée heure par heure');
  const grid = element('div', 'day-grid');
  addTimeLabels(grid);
  const offs = element('div', 'day-offs');
  offs.setAttribute('aria-hidden', 'true');
  const empty = element('p', 'empty-day', 'Aucun créneau ici : le suivi en ajoutera au fil de la journée, ou ajoute-en un à la main.');
  empty.hidden = true;
  grid.append(offs, element('div', 'day-blocks'), empty);
  timeline.append(grid);

  const rail = element('aside', 'day-rail');
  rail.setAttribute('aria-label', 'Synthèse de la journée');
  const total = element('div', 'rail-total');
  const figure = element('p', 'rail-figure');
  figure.append(element('strong', 'rail-value'), element('span', 'rail-target'));
  total.append(element('h3', 'subhead', 'Temps suivi'), figure, element('p', 'rail-share'));
  const pending = element('div', 'rail-pending');
  pending.hidden = true;
  const assign = button('button rail-assign', 'Attribuer');
  assign.id = 'rail-assign';
  assign.addEventListener('click', openFirstUnassigned);
  pending.append(element('p', 'rail-pending-value'), assign);
  const breakdown = element('section', 'rail-breakdown');
  const railEmpty = element('p', 'rail-empty', 'Rien à répartir pour l’instant.');
  railEmpty.hidden = true;
  breakdown.append(element('h3', 'subhead', 'Répartition'), element('ul', 'rail-rows'), railEmpty);
  rail.append(total, pending, breakdown);

  view.append(timeline, rail);
  view.dataset.ready = 'true';
  return view;
}
function renderRail(view) {
  const kept = included(entries);
  const total = kept.reduce((sum, entry) => sum + duration(entry), 0);
  const target = dayTarget();
  const unresolved = sendable(entries).filter(isUnassigned).reduce((sum, entry) => sum + duration(entry), 0);
  swapText(view.querySelector('.rail-value'), formatDuration(total));
  swapText(view.querySelector('.rail-target'), `/ ${formatDuration(target)}`);
  swapText(view.querySelector('.rail-share'), target ? `${Math.round(total / target * 100)} % de l’objectif` : '');
  swapText(view.querySelector('.rail-pending-value'), unresolved ? `${formatDuration(unresolved)} à attribuer` : '');
  reveal(view.querySelector('.rail-pending'), unresolved > 0);
  const assign = view.querySelector('#rail-assign');
  assign.title = `Ouvrir le premier créneau à attribuer (${formatDuration(unresolved)} le ${dayDate(asDate(selectedDay))}).`;
  assign.setAttribute('aria-label', `Attribuer : ouvrir le premier créneau à attribuer du ${dayDate(asDate(selectedDay))}.`);
  const rows = railRows(entries);
  reconcile(view.querySelector('.rail-rows'), rows, {
    create: createRailRow,
    update: updateRailRow,
    enter: node => reveal(node, true),
    ordered: true
  });
  reveal(view.querySelector('.rail-empty'), rows.length === 0);
  syncRailSelection();
}
function renderDay() {
  const view = dayScaffold();
  reconcile(view.querySelector('.day-offs'), offBands(), { create: createBand, update: updateBand, ordered: true });
  reconcile(view.querySelector('.day-blocks'), blockItems(entries, selectedDay), {
    create: createDayBlock,
    update: updateDayBlock,
    enter: enterBlock,
    ordered: true
  });
  reveal(view.querySelector('.empty-day'), entries.length === 0);
  placeNowLine(selectedDay === dateKey(new Date()) ? view.querySelector('.day-grid') : null);
  renderRail(view);
}

/* Semaine : sept en-têtes et sept colonnes posés une fois, mis à jour en place.
   Changer de semaine ne redessine plus la surface, il en change le contenu. */
function weekScaffold() {
  const calendar = $('#calendar');
  const known = calendar.querySelector('.week-view');
  if (known) return known;
  const view = element('div', 'week-view');
  const head = element('div', 'week-head');
  const timeline = element('div', 'week-timeline');
  addTimeLabels(timeline);
  for (let offset = 0; offset < 7; offset++) {
    const header = button('week-date');
    header.addEventListener('click', () => { if (header.dataset.date) openDay(header.dataset.date); });
    head.append(header);
    const column = element('div', 'week-column');
    /* Les sept colonnes se partagent toute la largeur restante après l’axe, à toute taille. */
    column.style.left = `calc(var(--axis) + (100% - var(--axis)) * ${offset} / 7)`;
    column.style.width = `calc((100% - var(--axis)) / 7)`;
    timeline.append(column);
  }
  const empty = element('p', 'week-empty', 'Aucun créneau cette semaine : ouvre une journée pour en ajouter un.');
  empty.hidden = true;
  timeline.append(empty);
  view.append(head, timeline);
  calendar.append(view);
  return view;
}
function renderWeek(first) {
  const view = weekScaffold();
  const head = view.querySelector('.week-head');
  const headers = head.querySelectorAll('.week-date');
  const columns = view.querySelectorAll('.week-column');
  let weekCount = 0;
  for (let offset = 0; offset < 7; offset++) {
    const date = addDays(first, offset);
    const key = dateKey(date);
    const selected = key === selectedDay;
    const header = headers[offset];
    header.dataset.date = key;
    swapText(header, capitalize(date.toLocaleDateString('fr-FR', { weekday: 'short', day: 'numeric' })));
    header.setAttribute('aria-label', dayAriaLabel(date, 'Ouvrir cette journée.'));
    /* La journée choisie se pose : le fond passe d’une teinte à l’autre, il ne saute pas. */
    header.classList.toggle('selected', selected);
    if (selected) header.setAttribute('aria-current', 'date');
    else header.removeAttribute('aria-current');
    const column = columns[offset];
    column.dataset.date = key;
    column.classList.toggle('selected', selected);
    const list = entriesOf(key);
    weekCount += list.length;
    renderBlocks(column, list, key);
  }
  reveal(view.querySelector('.week-empty'), weekCount === 0);
  /* Le filet de sélection est posé par syncSurface, une fois la semaine réellement affichée. */
}

/* Mois : les cases sont réutilisées d’un mois à l’autre, seules les semaines en trop
   sortent et les semaines manquantes entrent. */
function monthScaffold() {
  const calendar = $('#calendar');
  const known = calendar.querySelector('.month-view');
  if (known) return known;
  const view = element('div', 'month-view');
  for (const label of ['Lun.', 'Mar.', 'Mer.', 'Jeu.', 'Ven.', 'Sam.', 'Dim.']) view.append(element('div', 'weekday', label));
  calendar.append(view);
  return view;
}
function monthCell() {
  const cell = button('calendar-day');
  cell.append(element('span', 'calendar-date'), element('strong', 'calendar-total'));
  const warning = element('span', 'calendar-warning');
  warning.hidden = true;
  cell.append(warning);
  cell.addEventListener('click', () => { if (cell.dataset.date) openDay(cell.dataset.date); });
  return cell;
}
function updateDayCell(cell, date, inMonth) {
  const key = dateKey(date);
  const { count, total, remaining } = dayStats(key);
  const selected = key === selectedDay;
  cell.dataset.date = key;
  cell.classList.toggle('selected', selected);
  cell.classList.toggle('outside', !inMonth);
  cell.setAttribute('aria-label', dayAriaLabel(date, 'Ouvrir cette journée.'));
  if (selected) cell.setAttribute('aria-current', 'date');
  else cell.removeAttribute('aria-current');
  swapText(cell.querySelector('.calendar-date'), String(date.getDate()));
  swapText(cell.querySelector('.calendar-total'), count ? formatDuration(total) : '—');
  const warning = cell.querySelector('.calendar-warning');
  if (remaining) swapText(warning, `${formatDuration(remaining)} à attribuer`);
  reveal(warning, remaining > 0);
}
function renderMonth(first, anchor) {
  const view = monthScaffold();
  const weeks = Math.ceil(((new Date(anchor.getFullYear(), anchor.getMonth(), 1).getDay() + 6) % 7 + new Date(anchor.getFullYear(), anchor.getMonth() + 1, 0).getDate()) / 7);
  const wanted = weeks * 7;
  const cells = [...view.querySelectorAll('.calendar-day:not([data-exit])')];
  /* Une semaine de plus entre à sa place, devant les cases qui sont en train de sortir. */
  const leaving = view.querySelector('.calendar-day[data-exit]');
  for (let index = cells.length; index < wanted; index++) {
    const cell = monthCell();
    view.insertBefore(cell, leaving);
    cells.push(cell);
    reveal(cell, true);
  }
  for (let index = wanted; index < cells.length; index++) exitNode(cells[index]);
  for (let offset = 0; offset < wanted; offset++) updateDayCell(cells[offset], addDays(first, offset), addDays(first, offset).getMonth() === anchor.getMonth());
}
/* Chrome de la période : libellés et commandes de la barre. Aucune surface n’est
   montrée ni repliée ici — cette décision est prise une seule fois, par syncSurface. */
function renderChrome() {
  $('#selected-date').value = selectedDay;
  document.querySelectorAll('button[data-period]').forEach(node => node.setAttribute('aria-pressed', String(node.dataset.period === period)));
  placeViewThumb();
  const anchor = asDate(selectedDay);
  const first = period === 'month' ? startOfWeek(startOfMonth(anchor)) : startOfWeek(anchor);
  const last = addDays(first, 6);
  swapTextAt('#period-label', period === 'day'
    ? capitalize(longDate(anchor))
    : period === 'month'
      ? capitalize(anchor.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' }))
      : `${first.toLocaleDateString('fr-FR', { day: 'numeric', month: 'short' })} – ${last.toLocaleDateString('fr-FR', { day: 'numeric', month: 'short', year: 'numeric' })}`);
  $('#previous-period').setAttribute('aria-label', period === 'day' ? 'Jour précédent' : period === 'week' ? 'Semaine précédente' : 'Mois précédent');
  $('#next-period').setAttribute('aria-label', period === 'day' ? 'Jour suivant' : period === 'week' ? 'Semaine suivante' : 'Mois suivant');
  swapTextAt('#entries-title', period === 'day' ? capitalize(longDate(anchor)) : `Journée sélectionnée · ${dayDate(anchor)}`);
}

/* Une seule surface de planning est visible à la fois. La cible est décidée en un seul
   endroit, son contenu est construit avant qu’elle soit montrée, et toutes les autres
   sont repliées dans le même geste : aucune troisième surface ne peut apparaître entre
   deux vues, même pendant le croisé de 160 ms. */
function planningTarget() {
  if (!configured || onboardingOpen()) return 'onboarding';
  if (settingsOpen()) return 'settings';
  return period;
}
function buildSurface(target) {
  const anchor = asDate(selectedDay);
  if (target === 'day') renderDay();
  else if (target === 'week') renderWeek(startOfWeek(anchor));
  else if (target === 'month') renderMonth(startOfWeek(startOfMonth(anchor)), anchor);
}
function syncSurface() {
  if (isMini() || !selectedDay) return;
  const target = planningTarget();
  /* La vue qui arrive est complète avant d’être montrée : elle n’apparaît jamais à moitié. */
  buildSurface(target);
  const busy = target === 'onboarding' || target === 'settings';
  reveal($('#add'), !busy);
  reveal($('#validate-slot'), !busy);
  const calendar = $('#calendar');
  reveal(calendar.querySelector('.week-view'), target === 'week');
  reveal(calendar.querySelector('.month-view'), target === 'month');
  reveal(calendar, target === 'week' || target === 'month');
  reveal($('#entries-list'), target === 'day');
  /* L’échelle et le filet de sélection sont mesurés une fois la cible posée : la semaine
     réserve sa ligne d’en-têtes, et rien ne se mesure sur une surface encore repliée. */
  applyTimelineScale();
  if (target === 'week') {
    const head = $('.week-head');
    placeWeekUnderline(head, head?.querySelector('.week-date.selected'));
  }
}

/* ---------- calendrier surgissant du champ de date ---------- */

/* Les quarante-deux cases sont posées une fois : changer de mois n’en change que le contenu. */
function miniScaffold() {
  const grid = $('#mini-calendar');
  if (grid.dataset.ready === 'true') return grid;
  for (const label of ['Lu', 'Ma', 'Me', 'Je', 'Ve', 'Sa', 'Di']) {
    const weekday = element('span', 'mini-weekday', label);
    weekday.setAttribute('aria-hidden', 'true');
    grid.append(weekday);
  }
  for (let offset = 0; offset < 42; offset++) {
    const cell = button('mini-day');
    cell.addEventListener('click', () => { if (cell.dataset.date) openDay(cell.dataset.date); });
    grid.append(cell);
  }
  grid.dataset.ready = 'true';
  return grid;
}
function renderMini() {
  const grid = miniScaffold();
  swapTextAt('#mini-month-label', capitalize(miniMonth.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' })));
  const first = startOfWeek(miniMonth);
  const cells = grid.querySelectorAll('.mini-day');
  for (let offset = 0; offset < 42; offset++) {
    const date = addDays(first, offset);
    const key = dateKey(date);
    const selected = key === selectedDay;
    const cell = cells[offset];
    cell.dataset.date = key;
    swapText(cell, String(date.getDate()));
    cell.classList.toggle('selected', selected);
    cell.classList.toggle('outside', date.getMonth() !== miniMonth.getMonth());
    cell.setAttribute('aria-label', dayAriaLabel(date, 'Ouvrir cette journée.'));
    if (selected) cell.setAttribute('aria-current', 'date');
    else cell.removeAttribute('aria-current');
  }
}

/* ---------- suivi en cours ---------- */

/* La structure du chrono n’est construite qu’une fois : les deux-points ne doivent pas
   repartir de zéro à chaque seconde, sinon leur battement perd la cadence. */
function timerParts() {
  const timer = $('#timer');
  let tick = timer.querySelector('.tick');
  if (!tick || timer.childNodes.length !== 4) {
    tick = element('span', 'tick', ':');
    tick.setAttribute('aria-hidden', 'true');
    timer.replaceChildren(document.createTextNode('00'), tick, document.createTextNode('00'), element('span', undefined, ':00'));
  }
  return { timer, hours: timer.firstChild, minutes: tick.nextSibling, seconds: timer.lastChild };
}
function renderTimer() {
  const safe = Math.max(0, Math.floor(elapsedSeconds));
  const parts = timerParts();
  parts.hours.nodeValue = String(Math.floor(safe / 3600)).padStart(2, '0');
  parts.minutes.nodeValue = String(Math.floor(safe / 60) % 60).padStart(2, '0');
  parts.seconds.textContent = `:${String(safe % 60).padStart(2, '0')}`;
  parts.timer.setAttribute('aria-label', `${formatDuration(Math.floor(safe / 60))} sur le créneau en cours`);
}
function renderPauseButton() {
  const pause = $('#pause');
  const label = pause.querySelector('span');
  swapText(label, tracking.paused ? 'Reprendre le suivi' : 'Mettre en pause');
  pause.setAttribute('aria-pressed', String(Boolean(tracking.paused)));
  pause.title = tracking.paused ? 'Reprendre le suivi' : 'Mettre en pause';
  const icon = pause.querySelector('svg');
  if (!icon) return;
  const drawing = tracking.paused ? '<path d="m5 3 8 5-8 5z" fill="currentColor"/>' : '<path d="M5 3v10M11 3v10" stroke="currentColor" stroke-width="2"/>';
  /* L’icône ne se redessine que si l’état change, et elle se pose au lieu de permuter sèchement. */
  if (icon.innerHTML === drawing) return;
  icon.innerHTML = drawing;
  if (reduceMotion()) return;
  icon.classList.remove('icon-swap');
  void icon.getBoundingClientRect();
  icon.classList.add('icon-swap');
}
function renderTracking() {
  document.body.dataset.tracking = tracking.state ?? 'running';
  swapTextAt('#tracking-state', trackingLabels[tracking.state] ?? 'Suivi');
  const label = tracking.title || (tracking.state === 'no-repo' ? 'Dépôt Git introuvable' : tracking.state === 'not-configured' ? 'Dépôt non configuré' : 'Créneau à attribuer');
  swapTextAt('#tracking-label', label);
  swapText($('.mini-ticket'), tracking.workItem ? `#${tracking.workItem} · ` : '');
  $('#tracking-title').title = [
    tracking.bug ? `Ticket #${tracking.bug}` : null,
    tracking.workItem ? `Élément #${tracking.workItem}` : null,
    tracking.branch,
    label
  ].filter(Boolean).join(' · ');
  renderPauseButton();
  renderTimer();
}
/* Le chrono avance localement entre deux poussées de l’hôte, qui reste la référence. */
function restartTimer() {
  clearInterval(timerHandle);
  timerHandle = null;
  if (tracking.state !== 'running') return;
  timerHandle = setInterval(() => {
    elapsedSeconds += 1;
    renderTimer();
    /* Respiration du créneau en cours d’écriture : une fois par minute, sans minuterie de plus. */
    if (elapsedSeconds % 60 === 0) beatLiveBlock();
  }, 1000);
}
function applyTracking(next) {
  if (!next || typeof next !== 'object') return;
  tracking = next;
  elapsedSeconds = Number(next.elapsedSeconds) || 0;
  renderTracking();
  restartTimer();
}

/* ---------- connexions réelles ---------- */

function renderConnections(connections) {
  for (const [key, selector] of [['sevenpace', '#connection-sevenpace'], ['outlook', '#connection-outlook']]) {
    const info = connections?.[key];
    if (!info) continue;
    const node = $(selector);
    node.dataset.status = info.status ?? '';
    if (info.label) {
      swapText(node.querySelector('span'), info.label);
      node.title = info.label;
    }
  }
}
/* La liste des activités proposées est reconstruite depuis les réglages. */
function renderActivityOptions() {
  const select = form.elements.activity;
  const previous = select.value;
  const options = [['ticket', 'Ticket de développement']];
  for (const item of activities) options.push([item.key, item.workItem ? `${item.label} · #${item.workItem}` : item.label]);
  options.push(['unknown', 'À attribuer'], ['excluded', 'Pause / absence — exclure']);
  select.replaceChildren(...options.map(([value, text]) => {
    const option = element('option', undefined, text);
    option.value = value;
    return option;
  }));
  if (previous) setActivityValue(previous);
}
/* Une activité retirée des réglages retombe sur « À attribuer », jamais sur une valeur vide. */
function setActivityValue(key) {
  const select = form.elements.activity;
  select.value = key ?? '';
  if (!select.value) select.value = 'unknown';
}
function renderTarget() {
  swapText($('#target'), formatDuration(dayTarget()));
}

/* ---------- rendu global ---------- */

function render({ keepPreview = false } = {}) {
  if (isMini()) return;
  entries = entriesOf(selectedDay);
  entries.sort((a, b) => minutes(a.start) - minutes(b.start));
  const count = visibleEntryCount();
  dayChanged = painted.day !== selectedDay || painted.period !== period || (painted.entries === 0 && count > 0);
  renderChrome();
  renderMini();
  syncSurface();
  const kept = included(entries);
  const toSend = sendable(entries);
  const total = kept.reduce((sum, entry) => sum + duration(entry), 0);
  const remaining = toSend.filter(isUnassigned).reduce((sum, entry) => sum + duration(entry), 0);
  $('#unassigned-summary').classList.toggle('attention-text', remaining > 0);
  swapText($('#total'), formatDuration(total));
  swapText($('#unassigned-summary'), remaining
    ? `dont ${formatDuration(remaining)} à attribuer`
    : toSend.length ? 'Tout est attribué'
      : kept.length ? 'Tout est envoyé' : 'rien à envoyer pour l’instant');
  renderValidate({ remaining, toSend: toSend.length, kept: kept.length });
  if (keepPreview) { if (isPanelOpen($('#preview'))) renderPreview(); }
  else revealAt('#preview', false);
  painted = { day: selectedDay, period, entries: count };
  refreshNowLine();
}

/* Le bouton d’envoi n’est jamais « impossible mais cliquable ». Il a trois états :
   il attribue le temps qui reste, il prévisualise, ou il est réellement désactivé et dit pourquoi. */
let validateIntent = 'preview';
function renderValidate({ remaining, toSend, kept }) {
  const validate = $('#validate');
  const slot = $('#validate-slot');
  const day = dayDate(asDate(selectedDay));
  validateIntent = remaining > 0 ? 'assign' : toSend > 0 ? 'preview' : 'none';
  const hint = remaining
    ? `${formatDuration(remaining)} à attribuer le ${day} pour préparer l’envoi.`
    : toSend === 0
      ? kept
        ? `Tout le temps du ${day} est déjà envoyé dans 7pace : rien ne sera renvoyé.`
        : `Aucun temps à envoyer le ${day} : ajoute un créneau ou réinclus une activité.`
      : `Tout est attribué le ${day}. Vérifie l’aperçu avant l’envoi.`;
  swapTextAt('#validation-hint', hint);
  validate.disabled = validateIntent === 'none';
  validate.classList.toggle('primary', validateIntent === 'preview');
  swapText(validate, validateIntent === 'assign' ? `Attribuer ${formatDuration(remaining)}` : 'Prévisualiser l’envoi');
  validate.title = validateIntent === 'assign'
    ? `Ouvrir le premier créneau à attribuer (${formatDuration(remaining)} restantes le ${day}).`
    : validateIntent === 'preview'
      ? `Vérifier ce qui partira dans 7pace pour le ${day}.`
      : hint;
  validate.setAttribute('aria-label', validateIntent === 'assign'
    ? `Attribuer ${formatDuration(remaining)} : ouvrir le premier créneau à attribuer du ${day}.`
    : validateIntent === 'preview'
      ? `Prévisualiser l’envoi du ${day}.`
      : hint);
  /* L’infobulle d’une commande désactivée est portée par son support : Windows n’affiche
     pas celle d’un bouton inerte. */
  slot.title = validateIntent === 'none' ? hint : '';
  slot.dataset.disabled = String(validateIntent === 'none');
}

/* ---------- panneau d’édition ---------- */

function updateWorkItemField() {
  const needed = needsWorkItem(form.elements.activity.value);
  reveal($('#workitem-field'), needed);
  form.elements.workItem.required = needed;
}
function freeSlot() {
  const sorted = chronological(entries);
  for (const [start, end] of WORK_WINDOWS) {
    let cursor = start;
    for (const entry of sorted) {
      const from = minutes(entry.start), to = minutes(entry.end);
      if (to <= cursor || from >= end) continue;
      if (from > cursor) return [cursor, Math.min(from, cursor + 45)];
      cursor = Math.max(cursor, to);
    }
    if (cursor < end) return [cursor, Math.min(end, cursor + 45)];
  }
  return null;
}
function resetDeleteButton() {
  confirmingDelete = false;
  const remove = $('#delete-entry');
  swapText(remove, 'Supprimer le créneau');
  remove.classList.remove('armed');
}
function openEditor(entry) {
  if (entry && isSent(entry)) {
    announceAction(`Créneau envoyé dans 7pace le ${sentLabel(entry)} : il n’est plus modifiable et ne sera pas renvoyé.`);
    return;
  }
  const slot = entry ? null : freeSlot();
  if (!entry && !slot) {
    swapTextAt('#validation-hint', `Journée du ${dayDate(asDate(selectedDay))} complète : modifie un créneau existant.`);
    announceAction('Aucun créneau libre. Modifie une activité existante.');
    return;
  }
  editingId = entry?.id ?? null;
  swapTextAt('#editor-title', `${entry ? 'Modifier' : 'Ajouter'} · ${dayDate(asDate(selectedDay))}`);
  form.elements.start.value = entry?.start ?? asTime(slot[0]);
  form.elements.end.value = entry?.end ?? asTime(slot[1]);
  setActivityValue(entry?.activity ?? defaultActivity());
  form.elements.title.value = entry && entry.activity !== 'unknown' ? entry.title : '';
  form.elements.workItem.value = entry?.workItem ?? '';
  swapTextAt('#form-error', '');
  resetDeleteButton();
  reveal($('#delete-entry'), editingId !== null, { instant: !isPanelOpen($('#editor')) });
  updateWorkItemField();
  /* La surface de configuration laisse la place : l’inspecteur ne sert qu’au créneau et à l’aperçu. */
  if (isPanelOpen($('#settings'))) closeSettings({ silent: true });
  revealAt('#editor', true);
  /* L’éditeur porte désormais la consigne : le tiroir d’état se referme pour ne pas la doubler. */
  statusDetails.open = false;
  /* Le panneau s’ouvre sur place : on amène seulement le créneau visé dans la zone de
     planning, et la ligne du rail qui le porte passe en accent. */
  if (period === 'day' && entry) document.querySelector(`#entries-list .time-block[data-entry="${entry.id}"]`)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  syncRailSelection();
  form.elements[entry?.activity === 'unknown' ? 'activity' : 'start'].focus();
}
/* Ferme le panneau sans rien écrire et sans déplacer le focus. */
function discardEditor({ instant = false } = {}) {
  const wasOpen = isPanelOpen($('#editor'));
  revealAt('#editor', false, { instant });
  editingId = null;
  swapTextAt('#form-error', '');
  resetDeleteButton();
  syncRailSelection();
  return wasOpen;
}
function closeEditor() {
  discardEditor();
  const fallback = period === 'day' ? $('#add') : document.querySelector(`.week-date[data-date="${selectedDay}"], .calendar-day[data-date="${selectedDay}"]`);
  (fallback ?? $('#add'))?.focus();
}

/* ---------- aperçu et envoi réel ---------- */

function renderPreview() {
  const toSend = sendable(entries);
  const totals = new Map();
  for (const entry of toSend) {
    const label = entry.activity === 'ticket' ? (entry.title || activityNames.ticket) : activityLabel(entry.activity);
    const key = `${entry.workItem}:${label}`;
    const row = totals.get(key) ?? { id: entry.workItem, label, value: 0, slots: 0 };
    row.value += duration(entry);
    row.slots += 1;
    totals.set(key, row);
  }
  /* Les lignes sont réconciliées par élément de travail : rouvrir l’aperçu ne le repeint pas. */
  reconcile($('#preview-content'), Array.from(totals.entries())
    .sort(([, a], [, b]) => b.value - a.value)
    .map(([key, value]) => ({ key, value })), {
    create: () => {
      const row = element('div', 'preview-item');
      row.append(element('span', 'preview-label'), element('strong', 'preview-value'));
      return row;
    },
    update: (row, { id, label, value, slots }) => {
      swapText(row.querySelector('.preview-label'), `${label} · #${id}${slots > 1 ? ` · ${slots} créneaux` : ''}`);
      swapText(row.querySelector('.preview-value'), formatDuration(value));
    },
    enter: node => reveal(node, true)
  });
  swapTextAt('#preview-title', `Temps du ${dayDate(asDate(selectedDay))}`);
  const already = included(entries).filter(isSent);
  const alreadyTotal = already.reduce((sum, entry) => sum + duration(entry), 0);
  swapTextAt('#preview-note', toSend.length
    ? already.length
      ? `Déjà envoyé : ${formatDuration(alreadyTotal)}. Ces créneaux ne seront pas renvoyés.`
      : 'Ces temps seront écrits dans 7pace.'
    : already.length
      ? `Tout est envoyé : ${formatDuration(alreadyTotal)} déjà écrits dans 7pace, rien ne sera renvoyé.`
      : 'Aucun temps à envoyer : ajoute un créneau pour préparer un envoi.');
  /* Rien à envoyer : la commande est réellement désactivée et son support porte la raison. */
  const send = $('#send-day');
  const why = already.length
    ? 'Tout le temps de cette journée est déjà envoyé dans 7pace.'
    : 'Aucun temps à envoyer : ajoute un créneau ou réinclus une activité.';
  send.disabled = !toSend.length;
  send.title = toSend.length ? `Écrire ${formatDuration(toSend.reduce((sum, entry) => sum + duration(entry), 0))} dans 7pace.` : why;
  const holder = $('#send-slot');
  holder.title = toSend.length ? '' : why;
  holder.dataset.disabled = String(!toSend.length);
}

/* ---------- réglages ---------- */

const settingsForm = $('#settings-form');
const activityRows = $('#settings-activities');

const activityLabel = key => activityNames[key] ?? key;
const defaultActivity = () => (activities.some(item => item.key === 'meeting') ? 'meeting' : activities[0]?.key ?? 'ticket');
/* Une activité sans élément de travail configuré réclame un numéro saisi à la main. */
const needsWorkItem = activity => activity === 'ticket' || (activity !== 'unknown' && activity !== 'excluded' && !fixedTasks[activity]);

function applyActivities(list) {
  const known = (Array.isArray(list) ? list : []).filter(item => item && typeof item.key === 'string' && item.key);
  const source = known.length ? known : ACTIVITY_KEYS.map(key => ({ key, label: DEFAULT_ACTIVITY_LABELS[key], workItem: null }));
  activities = source.map(item => {
    const value = Number(item.workItem);
    return {
      key: item.key,
      label: String(item.label || DEFAULT_ACTIVITY_LABELS[item.key] || item.key),
      workItem: Number.isSafeInteger(value) && value > 0 ? value : null
    };
  });
  activityNames = { ...BASE_ACTIVITY_NAMES };
  fixedTasks = {};
  for (const item of activities) {
    activityNames[item.key] = item.label;
    if (item.workItem) fixedTasks[item.key] = item.workItem;
  }
  renderActivityOptions();
}
/* Les tâches fixes de bootstrap ne portent que les activités réellement configurées. */
function applyFixedTasks(map) {
  if (!map || typeof map !== 'object') return;
  for (const item of activities) {
    const value = Number(map[item.key]);
    if (Number.isSafeInteger(value) && value > 0) item.workItem = value;
  }
  fixedTasks = {};
  for (const item of activities) if (item.workItem) fixedTasks[item.key] = item.workItem;
  renderActivityOptions();
}
function applySettings(settings) {
  const next = settings && typeof settings === 'object' ? settings : {};
  settingsState = next;
  if (Array.isArray(next.workWindows) && next.workWindows.length) {
    WORK_WINDOWS = next.workWindows.map(([open, close]) => [Number(open), Number(close)]);
    if (WORK_WINDOWS.length > 2) WORK_WINDOWS = [[WORK_WINDOWS[0][0], WORK_WINDOWS[0][1]], [WORK_WINDOWS[WORK_WINDOWS.length - 1][0], WORK_WINDOWS[WORK_WINDOWS.length - 1][1]]];
  }
  if (Array.isArray(next.lunch) && next.lunch.length === 2) LUNCH = [Number(next.lunch[0]), Number(next.lunch[1])];
  if (typeof next.onboarded === 'boolean') onboarded = next.onboarded;
  applyActivities(next.activities);
  renderTarget();
}
/* Tant que le dépôt n’est pas configuré, la surface de planning porte la configuration guidée. */
function setConfigured(value) {
  configured = value !== false;
  syncSurface();
}

/* Les lignes d’activité servent aux réglages comme à la configuration guidée : le conteneur
   porte l’identifiant de son bouton d’ajout, rien n’est deviné. */
function activityRow(item, container = activityRows) {
  const row = element('div', 'activity-row');
  row.dataset.key = item.key;
  const label = element('input', 'activity-label');
  label.type = 'text';
  label.maxLength = 60;
  label.placeholder = 'Libellé';
  label.value = item.label ?? '';
  label.setAttribute('aria-label', `Libellé de l’activité ${item.label || item.key}`);
  const number = element('input', 'activity-workitem');
  number.type = 'number';
  number.min = '1';
  number.step = '1';
  number.placeholder = 'N°';
  number.value = item.workItem ? String(item.workItem) : '';
  number.setAttribute('aria-label', `Élément de travail de l’activité ${item.label || item.key}`);
  const remove = button('icon-button tiny');
  remove.innerHTML = '<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6"/></svg>';
  remove.setAttribute('aria-label', `Retirer l’activité ${item.label || item.key}`);
  remove.title = 'Retirer';
  remove.addEventListener('click', () => {
    exitNode(row);
    syncActivityRows(container);
    if (container === activityRows) syncSettingsDirty();
  });
  row.append(label, number, remove);
  return row;
}
function syncActivityRows(container = activityRows) {
  const rows = [...container.querySelectorAll('.activity-row:not([data-exit])')];
  let note = container.querySelector('.activity-empty');
  if (!note) {
    note = element('p', 'activity-empty', 'Aucune activité configurée : ajoute-en une, sinon l’éditeur ne proposera que les tickets.');
    note.hidden = true;
    container.append(note);
  }
  reveal(note, rows.length === 0);
  const remaining = ACTIVITY_KEYS.filter(key => !rows.some(row => row.dataset.key === key));
  /* Plus rien à ajouter : la commande est réellement désactivée et dit pourquoi. */
  const add = $(`#${container.dataset.add}`);
  if (add) {
    add.disabled = remaining.length === 0;
    add.title = remaining.length ? 'Ajouter une activité proposée dans l’éditeur' : 'Toutes les activités disponibles sont déjà configurées.';
  }
  return remaining;
}
/* Une activité ajoutée entre, une activité retirée sort : la liste ne saute pas. */
function addActivityRow(container) {
  const [key] = syncActivityRows(container);
  if (!key) return;
  const row = activityRow({ key, label: DEFAULT_ACTIVITY_LABELS[key], workItem: null }, container);
  row.hidden = true;
  container.append(row);
  reveal(row, true);
  syncActivityRows(container);
  if (container === activityRows) syncSettingsDirty();
  row.querySelector('.activity-workitem').focus();
  announce(`Activité « ${DEFAULT_ACTIVITY_LABELS[key]} » ajoutée. Indique son élément de travail, ou laisse-le vide.`);
}
/* Seules les activités réellement configurées occupent une ligne : les autres restent à ajouter. */
function renderActivityRows(list) {
  const rows = (Array.isArray(list) ? list : []).filter(item => item && typeof item.key === 'string'
    && (Number(item.workItem) > 0 || (item.label && item.label !== DEFAULT_ACTIVITY_LABELS[item.key])));
  activityRows.replaceChildren(...rows.map(item => activityRow(item, activityRows)));
  syncActivityRows(activityRows);
}
/* Lignes lues telles qu’affichées : un libellé vide ou un numéro non entier est refusé ici. */
function readActivityRows(container) {
  const list = [];
  for (const row of container.querySelectorAll('.activity-row:not([data-exit])')) {
    const label = row.querySelector('.activity-label').value.trim();
    const raw = row.querySelector('.activity-workitem').value.trim();
    if (!label) throw new Error('Donne un libellé à chaque activité, ou retire la ligne.');
    const workItem = raw ? Number(raw) : null;
    if (raw && (!Number.isSafeInteger(workItem) || workItem < 1)) throw new Error(`Le numéro d’élément de travail de « ${label} » doit être un entier positif.`);
    list.push({ key: row.dataset.key, label, workItem });
  }
  return list;
}
/* Une journée, une pause : les deux créneaux de travail en découlent et ne peuvent pas
   se contredire. Seule une heure absente est refusée ici ; une pause qui déborde de la
   journée est arbitrée par l’hôte, dont la phrase est affichée telle quelle. */
function readRhythm(fields) {
  const time = name => {
    const value = minutes(fields[name].value || '');
    if (!Number.isFinite(value)) throw new Error('Renseigne les heures de la journée et de la pause déjeuner.');
    return value;
  };
  const [dayStart, dayEnd, lunchStart, lunchEnd] = RHYTHM_FIELDS.map(time);
  return { workWindows: [[dayStart, lunchStart], [lunchEnd, dayEnd]], lunch: [lunchStart, lunchEnd] };
}
const rhythmText = fields => {
  const clock = name => fields[name].value || '—';
  return `${clock('dayStart')} – ${clock('lunchStart')} puis ${clock('lunchEnd')} – ${clock('dayEnd')}`;
};
function fillRhythm(fields, windows, lunch) {
  const first = windows[0] ?? WORK_WINDOWS[0];
  const last = windows[windows.length - 1] ?? first;
  fields.dayStart.value = asTime(Number(first[0]));
  fields.dayEnd.value = asTime(Number(last[1]));
  fields.lunchStart.value = asTime(Number(lunch[0]));
  fields.lunchEnd.value = asTime(Number(lunch[1]));
}
const RHYTHM_FIELDS = ['dayStart', 'dayEnd', 'lunchStart', 'lunchEnd'];

/* Vérification du dépôt : rien n’est enregistré, seule la branche est lue. La même
   mécanique sert à la configuration guidée et à la surface de configuration. */
function repoProbe(input, selector, onVerdict) {
  let timer = null;
  let run = 0;
  const settle = state => onVerdict?.(state);
  const probe = async path => {
    const mine = ++run;
    setVerdict(selector, 'pending', 'Lecture du dossier…', { quiet: true });
    try {
      const result = await host.call('probeRepo', { path });
      if (mine !== run) return;
      const ok = result?.ok === true;
      const ticket = Number(result?.ticket);
      const message = typeof result?.message === 'string' && result.message.trim()
        ? result.message.trim()
        : ok ? 'Dépôt lisible.' : 'Ce dossier n’est pas un dépôt Git lisible.';
      setVerdict(selector, ok ? 'good' : 'error', message);
      settle({
        ok,
        branch: typeof result?.branch === 'string' && result.branch.trim() ? result.branch.trim() : null,
        ticket: Number.isSafeInteger(ticket) && ticket > 0 ? ticket : null
      });
    } catch (error) {
      if (mine !== run) return;
      setVerdict(selector, 'error', hostErrorMessage(error));
      settle({ ok: false, branch: null, ticket: null });
    }
  };
  const reset = () => {
    clearTimeout(timer);
    timer = null;
    run += 1;
    settle({ ok: false, branch: null, ticket: null });
  };
  const schedule = () => {
    reset();
    const path = input.value.trim();
    if (!path) {
      setVerdict(selector, null, '', { quiet: true });
      return;
    }
    setVerdict(selector, 'pending', 'Lecture du dossier…', { quiet: true });
    timer = setTimeout(() => probe(path), PROBE_DEBOUNCE);
  };
  return { schedule, reset, probe };
}
/* Les deux essais réels : ils ne font que lire, et la phrase affichée est celle de l’hôte. */
async function runAzureProbe(trigger, fields, selector) {
  if (isPending(trigger)) return;
  setVerdict(selector, 'pending', 'Interrogation d’Azure DevOps…', { quiet: true });
  const organization = fields.azureOrganization.value.trim();
  const done = pending(trigger, 'Test…');
  try {
    const result = await host.call('probeAzure', { organization });
    const message = typeof result?.message === 'string' && result.message.trim()
      ? result.message.trim()
      : result?.ok ? 'Organisation joignable.' : 'Organisation injoignable.';
    setVerdict(selector, result?.ok === true ? 'good' : 'error', message);
  } catch (error) {
    setVerdict(selector, 'error', hostErrorMessage(error));
  } finally {
    done();
  }
}
async function runTokenProbe(trigger, fields, selector) {
  if (isPending(trigger)) return;
  setVerdict(selector, 'pending', 'Lecture seule sur l’API 7pace…', { quiet: true });
  const account = fields.sevenPaceAccount.value.trim();
  const done = pending(trigger, 'Test…');
  try {
    const result = await host.call('probeToken', { account, token: fields.token.value });
    const message = typeof result?.message === 'string' && result.message.trim()
      ? result.message.trim()
      : result?.ok ? 'Jeton accepté en lecture ; les droits d’écriture restent non prouvés.' : 'Le jeton n’a pas été accepté.';
    setVerdict(selector, result?.ok === true ? 'good' : 'error', message);
  } catch (error) {
    setVerdict(selector, 'error', hostErrorMessage(error));
  } finally {
    done();
  }
}
/* Choix du dossier : l’hôte ouvre le sélecteur Windows, la vérification suit aussitôt. */
async function chooseRepoFolder(trigger, input, probe) {
  if (isPending(trigger)) return;
  const done = pending(trigger, 'Ouverture…');
  try {
    const result = await host.call('chooseFolder', { current: input.value.trim() });
    const path = typeof result?.path === 'string' ? result.path.trim() : '';
    if (!path) {
      announce('Aucun dossier choisi : le dépôt indiqué n’a pas changé.');
      return;
    }
    input.value = path;
    if (input.form === settingsForm) syncSettingsDirty();
    probe.reset();
    await probe.probe(path);
  } catch (error) {
    setVerdict(input.form === settingsForm ? '#settings-repo-verdict' : '#onboard-repo-verdict', 'error', hostErrorMessage(error));
  } finally {
    done();
  }
}

function fillSettingsForm(settings) {
  const next = settings && typeof settings === 'object' ? settings : {};
  const fields = settingsForm.elements;
  const windows = Array.isArray(next.workWindows) && next.workWindows.length ? next.workWindows : WORK_WINDOWS;
  const lunch = Array.isArray(next.lunch) && next.lunch.length === 2 ? next.lunch : LUNCH;
  fields.repoPath.value = typeof next.repoPath === 'string' ? next.repoPath : '';
  fields.pollSeconds.value = String(Number(next.pollSeconds) || 30);
  fields.azureOrganization.value = typeof next.azureOrganization === 'string' ? next.azureOrganization : '';
  fields.sevenPaceAccount.value = typeof next.sevenPaceAccount === 'string' ? next.sevenPaceAccount : '';
  fillRhythm(fields, windows, lunch);
  fields.updateRepository.value = typeof next.updateRepository === 'string' ? next.updateRepository : '';
  fields.checkUpdates.checked = next.checkUpdates !== false;
  fields.token.value = '';
  renderActivityRows(Array.isArray(next.activities) ? next.activities : activities);
  renderSettingsRhythm();
  renderSettingsVersion();
  settingsBaseline = settingsSnapshot();
  syncSettingsDirty();
}
/* Les valeurs refusées côté hôte remontent telles quelles : ici on ne vérifie que le lisible. */
function readSettingsForm() {
  const fields = settingsForm.elements;
  const poll = Number(fields.pollSeconds.value);
  if (!Number.isSafeInteger(poll) || poll < 1) throw new Error('L’intervalle de relevé doit être un nombre entier de secondes.');
  const rhythm = readRhythm(fields);
  return {
    ...(settingsState ?? {}),
    repoPath: fields.repoPath.value.trim(),
    pollSeconds: poll,
    azureOrganization: fields.azureOrganization.value.trim(),
    sevenPaceAccount: fields.sevenPaceAccount.value.trim(),
    workWindows: rhythm.workWindows,
    lunch: rhythm.lunch,
    activities: readActivityRows(activityRows),
    updateRepository: fields.updateRepository.value.trim(),
    checkUpdates: fields.checkUpdates.checked,
    /* Le drapeau de configuration guidée n’est jamais remis à zéro par un enregistrement ordinaire. */
    onboarded
  };
}
/* Modifications non enregistrées : l’état est relu du formulaire, jamais deviné. */
let settingsBaseline = null;
let settingsDirty = false;
let cancelArmed = false;
function settingsSnapshot() {
  const fields = settingsForm.elements;
  return JSON.stringify([
    fields.repoPath.value,
    fields.pollSeconds.value,
    fields.azureOrganization.value,
    fields.sevenPaceAccount.value,
    ...RHYTHM_FIELDS.map(name => fields[name].value),
    fields.updateRepository.value,
    fields.checkUpdates.checked,
    fields.token.value,
    [...activityRows.querySelectorAll('.activity-row:not([data-exit])')].map(row => [
      row.dataset.key,
      row.querySelector('.activity-label').value,
      row.querySelector('.activity-workitem').value
    ])
  ]);
}
function syncSettingsDirty() {
  settingsDirty = settingsBaseline !== null && settingsSnapshot() !== settingsBaseline;
  reveal($('#settings-dirty'), settingsDirty);
  $('#settings-save').disabled = !settingsDirty;
  $('#settings-save-slot').dataset.disabled = String(!settingsDirty);
  $('#settings-save-slot').title = settingsDirty ? '' : 'Aucune modification à enregistrer.';
  if (!settingsDirty) resetSettingsCancel();
}
function resetSettingsCancel() {
  cancelArmed = false;
  const cancel = $('#close-settings');
  swapText(cancel, 'Annuler');
  cancel.classList.remove('armed');
}
function renderSettingsRhythm() {
  swapTextAt('#settings-rythme-preview', rhythmText(settingsForm.elements));
}
function renderSettingsVersion() {
  swapTextAt('#settings-version', `Version installée : ${appVersion ?? 'inconnue'}`);
}
const settingsRepo = repoProbe(settingsForm.elements.repoPath, '#settings-repo-verdict');
const settingsOpen = () => isPanelOpen($('#settings'));
async function openSettings(prefetched) {
  if (isMini() || onboardingOpen()) return;
  discardEditor({ instant: true });
  revealAt('#preview', false, { instant: true });
  statusDetails.open = false;
  revealAt('#settings', true);
  syncSurface();
  swapTextAt('#settings-error', '');
  clearFeedback('#settings-result');
  resetSettingsCancel();
  for (const selector of ['#settings-repo-verdict', '#settings-azure-verdict', '#settings-token-verdict']) {
    setVerdict(selector, null, '', { quiet: true });
  }
  if (prefetched) {
    applySettings(prefetched);
    fillSettingsForm(prefetched);
  }
  $('#settings-title').focus();
  announce('Configuration ouverte à la place du planning. Rien n’est enregistré avant « Enregistrer ».');
  if (!prefetched) {
    const done = pending($('#settings-save'), 'Chargement…');
    try {
      const result = await host.call('loadSettings', {});
      applySettings(result?.settings);
      fillSettingsForm(result?.settings);
      renderConnections(result?.connections);
      setConfigured(result?.configured !== false);
      if (typeof result?.onboarded === 'boolean') onboarded = result.onboarded;
      clearHostError();
      render();
    } catch (error) {
      swapTextAt('#settings-error', hostErrorMessage(error));
      announce(hostErrorMessage(error));
    } finally {
      done();
    }
  }
  if (settingsForm.elements.repoPath.value.trim()) settingsRepo.schedule();
}
function closeSettings({ silent = false } = {}) {
  settingsRepo.reset();
  revealAt('#settings', false);
  syncSurface();
  swapTextAt('#settings-error', '');
  clearFeedback('#settings-result');
  settingsForm.elements.token.value = '';
  settingsBaseline = null;
  syncSettingsDirty();
  resetSettingsCancel();
  /* Le planning revient avec son entrée habituelle : les créneaux se posent comme au premier affichage. */
  painted = { day: null, period: null, entries: 0 };
  if (!silent) {
    render();
    $('#open-settings')?.focus();
  }
}

/* ---------- configuration guidée ---------- */

/* Quatre questions posées à plat dans la surface de planning, puis un récapitulatif.
   Jamais de fenêtre modale, jamais de seconde fenêtre : le planning revient à la fin. */
const ONBOARD_STEPS = ['bienvenue', 'depot', 'rattachement', 'rythme', 'recapitulatif'];
const ONBOARD_TITLES = { bienvenue: 'Bienvenue', depot: 'Dépôt', rattachement: 'Rattachement', rythme: 'Rythme', recapitulatif: 'Récapitulatif' };
const ONBOARD_NEXT_LABEL = { bienvenue: 'Commencer', depot: 'Continuer', rattachement: 'Continuer', rythme: 'Continuer', recapitulatif: 'Terminer' };
/* Le récapitulatif reste la quatrième étape : l’indicateur n’en compte que quatre. */
const ONBOARD_POSITION = { bienvenue: 1, depot: 2, rattachement: 3, rythme: 4, recapitulatif: 4 };
const PROBE_DEBOUNCE = 400;
const onboardForm = $('#onboard-form');
const onboardActivities = $('#onboard-activities');
const onboardTimers = new Map();
let onboardIndex = 0;
let onboardRepo = { ok: false, branch: null, ticket: null };
const onboardingOpen = () => isPanelOpen($('#onboarding'));
const onboardStepNode = step => $(`#onboard-step-${step}`);

/* Verdict d’une vérification : la phrase de l’hôte telle quelle, doublée dans l’annonce.
   L’attente réutilise la barre discrète des commandes occupées, jamais une image qui tourne. */
function setVerdict(selector, kind, message, { quiet = false } = {}) {
  const node = $(selector);
  if (!node) return;
  swapText(node, message, { force: true });
  if (kind) node.dataset.kind = kind;
  else delete node.dataset.kind;
  if (kind === 'pending') {
    node.dataset.pending = 'true';
    node.setAttribute('aria-busy', 'true');
  } else {
    delete node.dataset.pending;
    node.removeAttribute('aria-busy');
  }
  if (message && !quiet) announce(message);
}

/* Croisé de 160 ms : la sortie se superpose à l’entrée, la hauteur de la surface ne saute pas. */
function swapOnboardStep(from, to, direction) {
  const incoming = onboardStepNode(to);
  for (const name of ONBOARD_STEPS) {
    if (name === to || name === from) continue;
    const other = onboardStepNode(name);
    clearTimeout(onboardTimers.get(other));
    onboardTimers.delete(other);
    delete other.dataset.entering;
    delete other.dataset.leaving;
    other.hidden = true;
  }
  clearTimeout(onboardTimers.get(incoming));
  onboardTimers.delete(incoming);
  delete incoming.dataset.leaving;
  incoming.hidden = false;
  const outgoing = from && from !== to ? onboardStepNode(from) : null;
  if (reduceMotion()) {
    if (outgoing) {
      delete outgoing.dataset.entering;
      delete outgoing.dataset.leaving;
      outgoing.hidden = true;
    }
    return;
  }
  incoming.dataset.entering = direction;
  onboardTimers.set(incoming, setTimeout(() => {
    delete incoming.dataset.entering;
    onboardTimers.delete(incoming);
  }, PANEL_MOTION));
  if (!outgoing || outgoing.hidden) return;
  delete outgoing.dataset.entering;
  outgoing.dataset.leaving = direction;
  onboardTimers.set(outgoing, setTimeout(() => {
    delete outgoing.dataset.leaving;
    outgoing.hidden = true;
    onboardTimers.delete(outgoing);
  }, PANEL_MOTION));
}
function syncOnboardChrome() {
  const step = ONBOARD_STEPS[onboardIndex];
  const position = ONBOARD_POSITION[step];
  for (const item of $('#onboard-indicator').children) {
    const rank = ONBOARD_POSITION[item.dataset.step];
    item.dataset.state = rank < position ? 'done' : rank === position ? 'current' : 'todo';
    if (rank === position) item.setAttribute('aria-current', 'step');
    else item.removeAttribute('aria-current');
  }
  reveal($('#onboard-back'), onboardIndex > 0);
  reveal($('#onboard-skip'), step === 'rattachement');
  const next = $('#onboard-next');
  swapText(next.querySelector('span'), ONBOARD_NEXT_LABEL[step]);
  /* Le dépôt doit être lisible pour continuer : la commande est alors réellement
     désactivée, et son support dit ce qui manque. */
  const blocked = step === 'depot' && !onboardRepo.ok;
  next.disabled = blocked;
  $('#onboard-next-slot').dataset.disabled = String(blocked);
  $('#onboard-next-slot').title = blocked ? 'Indique un dépôt Git lisible : la vérification doit réussir avant de continuer.' : '';
}
function goToStep(index, { direction } = {}) {
  const target = Math.max(0, Math.min(ONBOARD_STEPS.length - 1, index));
  const from = ONBOARD_STEPS[onboardIndex];
  const to = ONBOARD_STEPS[target];
  const way = direction ?? (target >= onboardIndex ? 'forward' : 'back');
  onboardIndex = target;
  swapOnboardStep(from === to ? null : from, to, way);
  swapTextAt('#onboard-error', '');
  syncOnboardChrome();
  if (to === 'rythme') renderRhythmPreview();
  if (to === 'recapitulatif') renderOnboardSummary();
  $(`#onboard-title-${to}`)?.focus();
  announce(to === 'recapitulatif'
    ? 'Récapitulatif de la configuration. Rien n’est enregistré avant « Terminer ».'
    : `Étape ${ONBOARD_POSITION[to]} sur 4 : ${ONBOARD_TITLES[to]}.`);
}

function renderRhythmPreview() { swapTextAt('#onboard-rythme-preview', rhythmText(onboardForm.elements)); }
function renderOnboardSummary() {
  const fields = onboardForm.elements;
  const count = onboardActivities.querySelectorAll('.activity-row:not([data-exit])').length;
  const branch = onboardRepo.ok
    ? [onboardRepo.branch, onboardRepo.ticket ? `ticket #${onboardRepo.ticket}` : null].filter(Boolean).join(' · ') || 'Lue au démarrage du suivi'
    : 'Vérification non concluante';
  const rows = [
    ['Dépôt observé', fields.repoPath.value.trim() || 'Aucun : le suivi ne démarrera pas'],
    ['Branche détectée', branch],
    ['Organisation Azure DevOps', fields.azureOrganization.value.trim() || 'Non renseignée : l’élément de travail restera à attribuer à la main'],
    ['Compte 7pace', fields.sevenPaceAccount.value.trim() || 'Non renseigné'],
    ['Jeton 7pace', fields.token.value
      ? 'Nouveau jeton à enregistrer ; les droits d’écriture restent non prouvés'
      : 'Aucun jeton saisi : aucun envoi ne sera possible'],
    ['Horaires', rhythmText(fields)],
    ['Relevé', `toutes les ${fields.pollSeconds.value || '30'} secondes`],
    ['Activités', count ? `${count} activité${count > 1 ? 's' : ''} proposée${count > 1 ? 's' : ''} dans l’éditeur` : 'Aucune : seuls les tickets seront proposés'],
    ['Calendrier Outlook', 'Non connecté : aucune réunion ne sera importée']
  ];
  /* Le récapitulatif est réconcilié par intitulé : il se met à jour, il ne se réécrit pas. */
  reconcile($('#onboard-summary'), rows.map(([term, value]) => ({ key: term, value: { term, value } })), {
    create: () => {
      const pair = element('div', 'summary-row');
      pair.append(element('dt'), element('dd'));
      return pair;
    },
    update: (pair, { term, value }) => {
      swapText(pair.querySelector('dt'), term);
      swapText(pair.querySelector('dd'), value);
    },
    enter: node => reveal(node, true)
  });
}

const onboardRepoProbe = repoProbe(onboardForm.elements.repoPath, '#onboard-repo-verdict', state => {
  onboardRepo = state ?? { ok: false, branch: null, ticket: null };
  syncOnboardChrome();
});


function fillOnboardForm(settings) {
  const next = settings && typeof settings === 'object' ? settings : {};
  const fields = onboardForm.elements;
  const windows = Array.isArray(next.workWindows) && next.workWindows.length ? next.workWindows : WORK_WINDOWS;
  const lunch = Array.isArray(next.lunch) && next.lunch.length === 2 ? next.lunch : LUNCH;
  fields.repoPath.value = typeof next.repoPath === 'string' ? next.repoPath : '';
  fields.pollSeconds.value = String(Number(next.pollSeconds) || 30);
  fields.azureOrganization.value = typeof next.azureOrganization === 'string' ? next.azureOrganization : '';
  fields.sevenPaceAccount.value = typeof next.sevenPaceAccount === 'string' ? next.sevenPaceAccount : '';
  fillRhythm(fields, windows, lunch);
  fields.token.value = '';
  /* Défauts neutres : les activités habituelles, sans le moindre numéro inventé. */
  const list = Array.isArray(next.activities) && next.activities.length
    ? next.activities
    : ACTIVITY_KEYS.map(key => ({ key, label: DEFAULT_ACTIVITY_LABELS[key], workItem: null }));
  onboardActivities.replaceChildren(...list
    .filter(item => item && typeof item.key === 'string' && item.key)
    .map(item => activityRow(item, onboardActivities)));
  syncActivityRows(onboardActivities);
  renderRhythmPreview();
}
function readOnboardForm() {
  const fields = onboardForm.elements;
  const poll = Number(fields.pollSeconds.value);
  if (!Number.isSafeInteger(poll) || poll < 1) throw new Error('L’intervalle de relevé doit être un nombre entier de secondes.');
  const rhythm = readRhythm(fields);
  return {
    ...(settingsState ?? {}),
    repoPath: fields.repoPath.value.trim(),
    pollSeconds: poll,
    azureOrganization: fields.azureOrganization.value.trim(),
    sevenPaceAccount: fields.sevenPaceAccount.value.trim(),
    workWindows: rhythm.workWindows,
    lunch: rhythm.lunch,
    activities: readActivityRows(onboardActivities),
    onboarded: true
  };
}

/* Un refus ramène sur l’étape qui porte la valeur refusée ; sa phrase est affichée telle quelle. */
function refusalStep(message) {
  const text = message.toLowerCase();
  if (/dép[oô]t|depot|\.git|chemin|dossier/.test(text)) return 'depot';
  if (/jeton|compte|organisation|azure|7pace/.test(text)) return 'rattachement';
  if (/horaire|plage|cr[ée]neau|chevauch|journ[ée]e|pause|déjeuner|dejeuner|relev|intervalle|activit/.test(text)) return 'rythme';
  return null;
}
function failOnboarding(message, step) {
  const target = step ? ONBOARD_STEPS.indexOf(step) : -1;
  if (target >= 0 && target !== onboardIndex) goToStep(target, { direction: 'back' });
  swapTextAt('#onboard-error', message);
  announce(message);
}
/* Premier démarrage seulement : après lui, la surface de configuration est le seul endroit
   où changer quoi que ce soit. */
function startOnboarding({ step = 'bienvenue', settings = null } = {}) {
  if (isMini()) return;
  discardEditor({ instant: true });
  revealAt('#preview', false, { instant: true });
  if (isPanelOpen($('#settings'))) closeSettings({ silent: true });
  statusDetails.open = false;
  fillOnboardForm(settings ?? settingsState);
  onboardRepoProbe.reset();
  for (const selector of ['#onboard-repo-verdict', '#onboard-azure-verdict', '#onboard-token-verdict']) {
    setVerdict(selector, null, '', { quiet: true });
  }
  swapTextAt('#onboard-error', '');
  onboardIndex = 0;
  reveal($('#onboarding'), true);
  syncSurface();
  goToStep(ONBOARD_STEPS.indexOf(step) < 0 ? 0 : ONBOARD_STEPS.indexOf(step));
  if (onboardForm.elements.repoPath.value.trim()) onboardRepoProbe.schedule();
}
function closeOnboarding() {
  onboardRepoProbe.reset();
  reveal($('#onboarding'), false);
  syncSurface();
  /* Le planning réapparaît avec son entrée habituelle : les créneaux se posent comme au premier affichage. */
  painted = { day: null, period: null, entries: 0 };
}
async function finishOnboarding() {
  const next = $('#onboard-next');
  if (isPending(next)) return;
  let settings;
  try {
    settings = readOnboardForm();
  } catch (error) {
    failOnboarding(hostErrorMessage(error), 'rythme');
    return;
  }
  const token = onboardForm.elements.token.value;
  swapTextAt('#onboard-error', '');
  const done = pending(next, 'Enregistrement…');
  let failure = null;
  try {
    const result = await host.call('saveSettings', { settings });
    const saved = result?.settings ?? settings;
    applySettings(saved);
    fillSettingsForm(saved);
    setConfigured(result?.configured !== false);
    onboarded = typeof result?.onboarded === 'boolean' ? result.onboarded : true;
    let connections = result?.connections;
    /* Le jeton part par son propre appel : il n’est jamais écrit dans le fichier de réglages. */
    if (token) {
      try {
        const stored = await host.call('saveToken', { token });
        connections = stored?.connections ?? connections;
        onboardForm.elements.token.value = '';
      } catch (error) {
        /* Les réglages sont écrits, seul le jeton a été refusé : la phrase de l’hôte, puis ce qui reste vrai. */
        failure = { message: `${hostErrorMessage(error)} Les réglages, eux, ont bien été enregistrés.`, step: 'rattachement' };
      }
    }
    renderConnections(connections);
    if (result?.tracking) applyTracking(result.tracking);
    clearHostError();
  } catch (error) {
    const message = hostErrorMessage(error);
    failure = { message, step: refusalStep(message) };
  }
  /* L’attente est relâchée avant tout retour en arrière : le libellé du bouton suit l’étape. */
  done();
  if (failure) {
    failOnboarding(failure.message, failure.step);
    return;
  }
  closeOnboarding();
  render();
  await loadVisible();
  const message = token
    ? 'Configuration enregistrée avec le jeton 7pace. Le suivi démarre ; rien ne partira dans 7pace sans ta validation.'
    : 'Configuration enregistrée. Le suivi démarre ; sans jeton 7pace, aucun envoi ne sera possible.';
  announce(message);
  setFeedback('#action-feedback', token ? 'Configuration enregistrée' : 'Configuration enregistrée, jeton encore absent');
}

/* ---------- mise à jour de l’application ---------- */

/* La version installée n’est connue qu’après une recherche : les deux endroits qui
   l’affichent sont écrits ensemble. */
function rememberVersion(info) {
  if (typeof info?.current !== 'string' || !info.current.trim()) return;
  appVersion = info.current.trim();
  swapTextAt('#update-version', `Version installée : ${appVersion}`);
  renderSettingsVersion();
}
const updateStatus = text => {
  swapTextAt('#update-status', text);
  swapTextAt('#settings-update-status', text);
};
/* Les notes de publication de GitHub sont un texte brut : on n’en garde qu’une ligne lisible. */
function firstNoteLine(notes) {
  if (typeof notes !== 'string') return '';
  for (const raw of notes.split('\n')) {
    if (raw.trimStart().startsWith('#')) continue;
    const line = raw.replace(/^\s*[*+>-]+\s*/, '').trim();
    if (line) return line.length > 120 ? `${line.slice(0, 117)}…` : line;
  }
  return '';
}
/* Bandeau réservé à une mise à jour réellement disponible : un échec n’y apparaît jamais. */
function renderUpdateBanner(info) {
  const banner = $('#update-banner');
  const latest = typeof info?.latest === 'string' ? info.latest.trim() : '';
  if (!info?.available || !latest) {
    reveal(banner, false);
    return;
  }
  swapTextAt('#update-banner-title', appVersion
    ? `Version ${latest} disponible (installée : ${appVersion}).`
    : `Version ${latest} disponible.`);
  swapTextAt('#update-banner-note', firstNoteLine(info.notes));
  const apply = $('#apply-update');
  apply.disabled = false;
  swapText(apply, 'Mettre à jour');
  applyingUpdate = false;
  reveal(banner, true);
}
async function checkUpdate({ manual, trigger = null }) {
  if (checkingUpdate || applyingUpdate) return;
  checkingUpdate = true;
  /* L’attente se voit sur le bouton qui l’a lancée ; une vérification de fond reste muette. */
  const done = manual ? pending(trigger ?? $('#check-update'), 'Vérification…') : () => {};
  if (manual) updateStatus('Vérification en cours…');
  try {
    const info = await host.call('checkUpdate', {});
    rememberVersion(info);
    const failure = typeof info?.error === 'string' && info.error.trim() ? info.error.trim() : null;
    if (failure) {
      /* Vérification en échec : le message reste affiché, la bannière ne bouge pas. */
      updateStatus(failure);
      if (manual) announce(failure);
      return;
    }
    const latest = typeof info?.latest === 'string' ? info.latest.trim() : '';
    const message = info?.available && latest
      ? `Version ${latest} disponible.`
      : appVersion ? `Aucune mise à jour : ${appVersion} est la dernière version.` : 'Aucune mise à jour disponible.';
    updateStatus(message);
    renderUpdateBanner(info);
    if (manual) announce(message);
  } catch (error) {
    updateStatus(hostErrorMessage(error));
    if (manual) announce(hostErrorMessage(error));
  } finally {
    checkingUpdate = false;
    done();
  }
}

/* ---------- interactions ---------- */

/* Bascule d’une fenêtre à l’autre : la surface sortante s’efface d’abord.
   Les deux fenêtres gardent leur document — l’hôte se contente de masquer l’une et de
   montrer l’autre. La marque de passage doit donc être temporaire : sans cela la surface
   resterait posée à l’opacité 0 par l’animation de sortie, et la fenêtre reviendrait vide. */
const HANDOVER_LIFE = 400;
let handoverTimer = null;
function markHandover() {
  if (reduceMotion()) return;
  document.body.dataset.handover = 'true';
  clearTimeout(handoverTimer);
  handoverTimer = setTimeout(endHandover, HANDOVER_LIFE);
}
function endHandover() {
  clearTimeout(handoverTimer);
  handoverTimer = null;
  delete document.body.dataset.handover;
}
/* La fenêtre revient : on efface la marque de passage et on relit la géométrie, car elle
   a pu être redimensionnée ou agrandie pendant qu’elle était masquée — aucun événement
   de redimensionnement n’arrive à une fenêtre cachée. */
function restoreWindow() {
  endHandover();
  if (isMini()) return;
  applyTimelineScale();
  placeViewThumb();
  const head = document.querySelector('.week-head');
  if (head) placeWeekUnderline(head, head.querySelector('.week-date.selected'));
}
document.addEventListener('visibilitychange', () => { if (!document.hidden) restoreWindow(); });
window.addEventListener('focus', restoreWindow);
window.addEventListener('pageshow', restoreWindow);
$('#toggle-view').addEventListener('click', async () => {
  const toggle = $('#toggle-view');
  if (isPending(toggle)) return;
  const mini = isMini();
  const done = pending(toggle, mini ? 'Ouverture…' : 'Bascule…');
  markHandover();
  try {
    await host.call(mini ? 'showMain' : 'showMini', {});
    clearHostError();
  } catch (error) {
    endHandover();
    reportHostError(error);
  } finally {
    done();
  }
});
function syncViewToggle() {
  const mini = isMini();
  const toggle = $('#toggle-view');
  const label = toggle.querySelector('span');
  swapText(label, mini ? 'Ouvrir' : 'Mini-chrono');
  toggle.setAttribute('aria-label', mini ? 'Ouvrir la vue complète' : 'Passer au mini-chrono');
  toggle.title = mini ? 'Ouvrir la vue complète' : 'Passer au mini-chrono';
}
/* Changement de vue : la surface se croise dans le sens du parcours jour → semaine → mois. */
const PERIOD_ORDER = ['day', 'week', 'month'];
document.querySelectorAll('button[data-period]').forEach(node => node.addEventListener('click', () => {
  if (isPending(node)) return;
  const next = node.dataset.period;
  if (next !== period) burst($('.surface-scroll'), 'swap', PERIOD_ORDER.indexOf(next) > PERIOD_ORDER.indexOf(period) ? 'forward' : 'back');
  period = next;
  render();
  loadVisible({ trigger: node });
  announce(`Vue ${period === 'day' ? 'jour' : period === 'week' ? 'semaine' : 'mois'} affichée.`);
}));
$('#selected-date').addEventListener('change', event => { if (event.target.value) selectDay(event.target.value); });
for (const [selector, direction] of [['#previous-period', -1], ['#next-period', 1]]) {
  $(selector).addEventListener('click', () => {
    const trigger = $(selector);
    if (isPending(trigger)) return;
    const date = asDate(selectedDay);
    if (period === 'month') { date.setDate(1); date.setMonth(date.getMonth() + direction); }
    else date.setDate(date.getDate() + direction * (period === 'week' ? 7 : 1));
    /* Le planning se déplace de quelques pixels dans le sens du parcours. */
    burst($('.surface-scroll'), 'travel', direction > 0 ? 'next' : 'prev');
    /* La flèche porte l’attente : c’est elle qui a demandé la plage. */
    selectDay(dateKey(date), period, { trigger });
  });
}
for (const [selector, direction] of [['#mini-prev-month', -1], ['#mini-next-month', 1]]) {
  $(selector).addEventListener('click', () => {
    const trigger = $(selector);
    if (isPending(trigger)) return;
    /* Parcourir les mois suppose le calendrier visible : on le déplie si besoin. */
    calendarDisclosure.open = true;
    miniMonth = new Date(miniMonth.getFullYear(), miniMonth.getMonth() + direction, 1, 12);
    burst($('#mini-calendar'), 'travel', direction > 0 ? 'next' : 'prev');
    renderMini();
    loadVisible({ trigger });
    announce(`Mini-calendrier sur ${miniMonth.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' })}. La journée affichée n’a pas changé.`);
  });
}
$('#add').addEventListener('click', () => openEditor(null));
$('#cancel-editor').addEventListener('click', closeEditor);
form.elements.activity.addEventListener('change', () => { form.elements.workItem.value = ''; updateWorkItemField(); });
form.addEventListener('submit', async event => {
  event.preventDefault();
  const start = form.elements.start.value, end = form.elements.end.value;
  const from = minutes(start), to = minutes(end);
  const fail = message => { swapTextAt('#form-error', message); };
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) return fail('La fin doit être après le début du créneau.');
  if (!WORK_WINDOWS.some(([open, close]) => from >= open && to <= close)) return fail(`Choisis un créneau entre ${workWindowsLabel()}.`);
  if (entries.some(entry => entry.id !== editingId && from < minutes(entry.end) && to > minutes(entry.start))) return fail('Ce créneau chevauche une autre activité. Ajuste les horaires pour ne pas compter deux fois le même temps.');
  const activity = form.elements.activity.value;
  const manual = needsWorkItem(activity);
  const workItem = manual ? Number(form.elements.workItem.value) : fixedTasks[activity] ?? null;
  if (manual && (!Number.isSafeInteger(workItem) || workItem < 1)) return fail('Indique un numéro d’élément de travail entier et positif.');
  const existing = entries.find(item => item.id === editingId);
  const entry = {
    id: editingId,
    start, end, activity,
    title: form.elements.title.value.trim() || activityLabel(activity),
    workItem,
    source: existing?.source ?? 'manual',
    sentAt: null
  };
  const day = selectedDay;
  const save = form.querySelector('.save');
  if (isPending(save)) return;
  const done = pending(save, 'Enregistrement…');
  try {
    const result = await host.call('saveEntry', { date: day, entry });
    applyDay(result);
    clearHostError();
    render();
    closeEditor();
    /* L’hôte attribue l’identifiant : on retrouve le créneau écrit par ses horaires. */
    const saved = entriesOf(day).find(item => item.start === entry.start && item.end === entry.end);
    if (saved && day === selectedDay) highlightBlock(saved.id);
    announce(`Créneau du ${dayDate(asDate(day))} enregistré.`);
    setFeedback('#action-feedback', 'Créneau enregistré');
  } catch (error) {
    /* Refus de l’hôte : son message est affiché tel quel, rien n’est écrit localement. */
    fail(hostErrorMessage(error));
    announce(hostErrorMessage(error));
  } finally {
    done();
  }
});
$('#delete-entry').addEventListener('click', async () => {
  if (editingId === null) return;
  const remove = $('#delete-entry');
  if (isPending(remove)) return;
  if (!confirmingDelete) {
    confirmingDelete = true;
    swapText(remove, 'Confirmer la suppression');
    remove.classList.add('armed');
    announce('Appuie une seconde fois pour supprimer ce créneau.');
    return;
  }
  const day = selectedDay, id = editingId;
  const done = pending(remove, 'Suppression…');
  try {
    const result = await host.call('deleteEntry', { date: day, id });
    clearHostError();
    /* Sortie jouée avant le retrait : le créneau s’efface, il ne disparaît pas d’un coup. */
    if (day === selectedDay) await collapseBlock(id);
    applyDay(result);
    render();
    closeEditor();
    announce(`Créneau du ${dayDate(asDate(day))} supprimé.`);
    setFeedback('#action-feedback', 'Créneau supprimé');
  } catch (error) {
    swapTextAt('#form-error', hostErrorMessage(error));
    announce(hostErrorMessage(error));
    resetDeleteButton();
  } finally {
    done();
  }
});
$('#pause').addEventListener('click', async () => {
  const pauseButton = $('#pause');
  if (isPending(pauseButton)) return;
  const next = !tracking.paused;
  const done = pending(pauseButton);
  try {
    const result = await host.call('setPaused', { paused: next });
    applyTracking(result?.tracking);
    clearHostError();
    announce(next ? 'Suivi mis en pause.' : 'Suivi repris.');
    setFeedback('#action-feedback', next ? 'Suivi en pause' : 'Suivi repris');
  } catch (error) {
    reportHostError(error);
  } finally {
    done();
  }
});
/* Une seule commande, trois intentions : attribuer ce qui reste, prévisualiser, ou rien
   du tout — et dans ce dernier cas elle est désactivée, donc ce clic n’arrive jamais. */
$('#validate').addEventListener('click', () => {
  if (validateIntent === 'none') return;
  if (validateIntent === 'assign') { openFirstUnassigned(); return; }
  swapTextAt('#send-result', '');
  $('#send-result').classList.remove('good', 'error');
  renderPreview();
  revealAt('#preview', true);
  statusDetails.open = false;
  $('#preview-title').focus();
  announce('Aperçu prêt. Rien n’est envoyé avant confirmation.');
});
$('#send-day').addEventListener('click', async () => {
  const send = $('#send-day');
  if (sending || isPending(send)) return;
  const result = $('#send-result');
  const day = selectedDay;
  if (!sendable(entries).length) {
    announceAction(included(entries).length
      ? 'Tout le temps de cette journée est déjà envoyé dans 7pace.'
      : 'Aucun temps à envoyer : ajoute un créneau ou réinclus une activité.');
    return;
  }
  sending = true;
  const done = pending(send, 'Envoi en cours…');
  swapText(result, '');
  result.classList.remove('good', 'error');
  announce(`Envoi du ${dayDate(asDate(day))} dans 7pace en cours.`);
  try {
    const response = await host.call('submitDay', { date: day });
    /* Un échec d’écriture reste un échec : jamais présenté comme envoyé. */
    const message = typeof response?.message === 'string' && response.message.trim()
      ? response.message.trim()
      : response?.ok ? 'Temps envoyé dans 7pace.' : 'L’envoi a échoué : aucun temps n’a été écrit dans 7pace.';
    swapText(result, message);
    result.classList.toggle('good', Boolean(response?.ok));
    result.classList.toggle('error', !response?.ok);
    announce(message);
    /* L’écriture réelle garde sa trace dans l’aperçu ; la barre d’état n’en donne que l’écho. */
    setFeedback('#action-feedback', response?.ok ? 'Temps envoyé dans 7pace' : 'Envoi refusé', response?.ok ? 'good' : 'error');
    if (response?.ok) clearHostError();
    else reportHostError(new Error(message));
  } catch (error) {
    swapText(result, hostErrorMessage(error));
    result.classList.remove('good');
    result.classList.add('error');
    setFeedback('#action-feedback', 'Envoi impossible', 'error');
    reportHostError(error);
  } finally {
    sending = false;
    done();
    await loadVisible();
    if (isPanelOpen($('#preview'))) renderPreview();
  }
});
$('#close-preview').addEventListener('click', () => { revealAt('#preview', false); $('#validate').focus(); });
$('#open-settings').addEventListener('click', () => openSettings(null));
$('#onboard-back').addEventListener('click', () => goToStep(onboardIndex - 1, { direction: 'back' }));
$('#onboard-skip').addEventListener('click', () => {
  goToStep(onboardIndex + 1);
  announce('Rattachement reporté : pas de rapprochement automatique de l’élément de travail, et aucun envoi tant que le jeton manque.');
});
/* Entrée valide l’étape : le formulaire porte les cinq volets, jamais un bouton par champ. */
onboardForm.addEventListener('submit', event => {
  event.preventDefault();
  if (isPending($('#onboard-next'))) return;
  const step = ONBOARD_STEPS[onboardIndex];
  if (step === 'depot' && !onboardRepo.ok) return;
  if (step === 'recapitulatif') {
    finishOnboarding();
    return;
  }
  goToStep(onboardIndex + 1);
});
$('#onboard-repo').addEventListener('input', () => onboardRepoProbe.schedule());
$('#onboard-browse').addEventListener('click', () => chooseRepoFolder($('#onboard-browse'), onboardForm.elements.repoPath, onboardRepoProbe));
$('#onboard-test-azure').addEventListener('click', () => runAzureProbe($('#onboard-test-azure'), onboardForm.elements, '#onboard-azure-verdict'));
$('#onboard-test-token').addEventListener('click', () => runTokenProbe($('#onboard-test-token'), onboardForm.elements, '#onboard-token-verdict'));
for (const name of RHYTHM_FIELDS) onboardForm.elements[name].addEventListener('input', renderRhythmPreview);
$('#onboard-add-activity').addEventListener('click', () => addActivityRow(onboardActivities));
$('#add-activity').addEventListener('click', () => addActivityRow(activityRows));
$('#settings-repo').addEventListener('input', () => settingsRepo.schedule());
$('#settings-browse').addEventListener('click', () => chooseRepoFolder($('#settings-browse'), settingsForm.elements.repoPath, settingsRepo));
$('#settings-test-azure').addEventListener('click', () => runAzureProbe($('#settings-test-azure'), settingsForm.elements, '#settings-azure-verdict'));
$('#settings-test-token').addEventListener('click', () => runTokenProbe($('#settings-test-token'), settingsForm.elements, '#settings-token-verdict'));
$('#settings-check-update').addEventListener('click', () => checkUpdate({ manual: true, trigger: $('#settings-check-update') }));
for (const name of RHYTHM_FIELDS) settingsForm.elements[name].addEventListener('input', renderSettingsRhythm);
/* Toute saisie dans la configuration se compare à l’état chargé : la mention de
   modifications non enregistrées ne ment jamais. */
settingsForm.addEventListener('input', syncSettingsDirty);
settingsForm.addEventListener('change', syncSettingsDirty);
/* « Annuler » demande confirmation en ligne quand il y a quelque chose à perdre. */
$('#close-settings').addEventListener('click', () => {
  if (settingsDirty && !cancelArmed) {
    cancelArmed = true;
    swapText($('#close-settings'), 'Confirmer l’abandon');
    $('#close-settings').classList.add('armed');
    announce('Des modifications ne sont pas enregistrées. Appuie une seconde fois pour les abandonner.');
    return;
  }
  closeSettings();
  announce('Configuration fermée. Aucun changement n’a été enregistré.');
});
settingsForm.addEventListener('submit', async event => {
  event.preventDefault();
  const fail = message => { swapTextAt('#settings-error', message); clearFeedback('#settings-result'); announce(message); };
  let next;
  try {
    next = readSettingsForm();
  } catch (error) {
    return fail(hostErrorMessage(error));
  }
  const token = settingsForm.elements.token.value;
  const save = $('#settings-save');
  if (isPending(save)) return;
  const done = pending(save, 'Enregistrement…');
  try {
    const result = await host.call('saveSettings', { settings: next });
    let connections = result?.connections;
    /* Le jeton part par son propre appel : il n’est jamais écrit dans le fichier de réglages. */
    if (token) {
      const stored = await host.call('saveToken', { token });
      connections = stored?.connections ?? connections;
      settingsForm.elements.token.value = '';
    }
    applySettings(result?.settings ?? next);
    fillSettingsForm(result?.settings ?? next);
    renderConnections(connections);
    setConfigured(result?.configured !== false);
    if (typeof result?.onboarded === 'boolean') onboarded = result.onboarded;
    if (result?.tracking) applyTracking(result.tracking);
    swapTextAt('#settings-error', '');
    clearHostError();
    render();
    await loadVisible();
    announce(token ? 'Réglages et jeton enregistrés.' : 'Réglages enregistrés.');
    setFeedback('#settings-result', token ? 'Réglages et jeton enregistrés' : 'Réglages enregistrés');
    setFeedback('#action-feedback', token ? 'Réglages et jeton enregistrés' : 'Réglages enregistrés');
  } catch (error) {
    /* Refus de l’hôte : sa phrase est affichée telle quelle, rien n’a été enregistré. */
    fail(hostErrorMessage(error));
  } finally {
    done();
  }
});
$('#remove-token').addEventListener('click', async () => {
  const remove = $('#remove-token');
  if (isPending(remove)) return;
  const done = pending(remove, 'Retrait…');
  try {
    const result = await host.call('saveToken', { token: '' });
    renderConnections(result?.connections);
    settingsForm.elements.token.value = '';
    swapTextAt('#settings-error', '');
    setVerdict('#settings-token-verdict', null, '', { quiet: true });
    clearHostError();
    announce('Jeton 7pace retiré. Aucun envoi ne sera possible avant d’en enregistrer un nouveau.');
    setFeedback('#settings-result', 'Jeton retiré : enregistre-en un nouveau pour pouvoir envoyer.');
  } catch (error) {
    swapTextAt('#settings-error', hostErrorMessage(error));
    clearFeedback('#settings-result');
    announce(hostErrorMessage(error));
  } finally {
    done();
  }
});
$('#check-update').addEventListener('click', () => checkUpdate({ manual: true }));
$('#apply-update').addEventListener('click', async () => {
  const apply = $('#apply-update');
  if (applyingUpdate || isPending(apply)) return;
  applyingUpdate = true;
  const note = $('#update-banner-note');
  const done = pending(apply, 'Mise à jour…');
  swapText(note, 'Téléchargement de la nouvelle version…');
  announce('Mise à jour en cours.');
  try {
    const result = await host.call('applyUpdate', {});
    const message = typeof result?.message === 'string' && result.message.trim()
      ? result.message.trim()
      : result?.ok ? 'Mise à jour prête.' : 'La mise à jour a échoué : rien n’a été installé.';
    updateStatus(message);
    if (result?.ok) {
      /* L’hôte enchaîne sur la fermeture : plus d’attente affichée, et plus rien à presser. */
      done();
      apply.disabled = true;
      swapText(apply, 'Mise à jour prête');
      swapText(note, `${message} L’application va se fermer puis se rouvrir.`);
      announce(`${message} L’application va se fermer puis se rouvrir.`);
      return;
    }
    swapText(note, message);
    reportHostError(new Error(message));
  } catch (error) {
    swapText(note, hostErrorMessage(error));
    updateStatus(hostErrorMessage(error));
    reportHostError(error);
  }
  done();
  applyingUpdate = false;
  swapText(apply, 'Réessayer');
});
$('#dismiss-update').addEventListener('click', () => {
  reveal($('#update-banner'), false);
  announce('Mise à jour reportée. Elle reste proposée dans le tiroir d’état.');
  statusDetails.focus();
});
/* L’échec reste affiché jusqu’à ce qu’il soit remplacé ou masqué à la main. */
$('#dismiss-host-error').addEventListener('click', () => {
  clearHostError();
  announce('Message d’erreur masqué.');
  statusDetails.focus();
});
/* Échap referme, du plus contextuel au plus périphérique : éditeur, aperçu, puis les tiroirs. */
document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' || event.defaultPrevented) return;
  if (isPanelOpen($('#editor'))) {
    closeEditor();
    announce('Édition abandonnée. Rien n’a été enregistré.');
  } else if (settingsOpen()) {
    $('#close-settings').click();
  } else if (isPanelOpen($('#preview'))) {
    revealAt('#preview', false);
    $('#validate').focus();
  } else if (isMini()) return;
  else if (statusDetails.open) {
    statusDetails.open = false;
    statusDetails.focus();
  } else if (calendarDisclosure.open) {
    calendarDisclosure.open = false;
    calendarDisclosure.focus();
  } else return;
  event.preventDefault();
});

/* ---------- poussées de l’hôte ---------- */

host.on('tracking', payload => applyTracking(payload));
host.on('day', payload => {
  if (!payload || typeof payload.date !== 'string') return;
  applyDay(payload);
  /* L’éditeur ouvert n’est pas refermé : seule la surface de planning est redessinée. */
  render({ keepPreview: true });
});
/* Vérification de fond de l’hôte : elle n’interrompt rien, elle propose. */
host.on('update', payload => {
  rememberVersion(payload);
  if (applyingUpdate) return;
  const failure = typeof payload?.error === 'string' && payload.error.trim() ? payload.error.trim() : null;
  if (failure) {
    updateStatus(failure);
    return;
  }
  const latest = typeof payload?.latest === 'string' ? payload.latest.trim() : '';
  if (!payload?.available || !latest) return;
  renderUpdateBanner(payload);
  updateStatus(`Version ${latest} disponible.`);
  announce(`Version ${latest} disponible. La proposition de mise à jour est affichée en bas de la fenêtre.`);
});

/* ---------- démarrage ---------- */

if (new URLSearchParams(location.search).get('view') === 'mini') document.body.dataset.view = 'mini';
document.body.dataset.tracking = 'loading';
syncViewToggle();
if (isMini()) { calendarDisclosure.open = false; statusDetails.open = false; }
/* Démarrage : les quatre surfaces arrivent en séquence, une seule fois par lancement. */
if (!isMini() && !reduceMotion()) {
  document.body.dataset.boot = 'in';
  setTimeout(() => { delete document.body.dataset.boot; }, 260);
}
/* La pastille de vue et l’échelle sont mesurées : elles attendent la police pour ne pas
   se poser à côté. */
document.fonts?.ready.then(() => { placeViewThumb(); applyTimelineScale(); });
/* Redimensionnement, maximisation, passage mini ↔ complet : la géométrie est relue une
   fois par image, jamais à chaque événement — l’axe se réajuste sans reflow visible. */
let resizePending = false;
window.addEventListener('resize', () => {
  if (isMini() || resizePending) return;
  resizePending = true;
  requestAnimationFrame(() => {
    resizePending = false;
    if (isMini()) return;
    applyTimelineScale();
    placeViewThumb();
    const head = document.querySelector('.week-head');
    if (head) placeWeekUnderline(head, head.querySelector('.week-date.selected'));
  });
});
/* Le filet de l’heure courante avance seul : aucun redessin du planning pour autant. */
setInterval(refreshNowLine, NOW_LINE_REFRESH);

(async () => {
  /* Un démarrage lent montre la vraie grille estompée plutôt qu’une surface vide. */
  const booting = watchSlowCall(() => {
    if (selectedDay) return;
    selectedDay = dateKey(new Date());
    miniMonth = startOfMonth(asDate(selectedDay));
    render();
    /* Le squelette ne compte pas comme une journée dessinée : l’entrée des blocs reste à jouer. */
    painted = { day: null, period: null, entries: 0 };
  });
  /* Deux lectures en parallèle : bootstrap pour le suivi, loadSettings pour les libellés d’activités. */
  const [booted, loaded] = await Promise.allSettled([host.call('bootstrap', {}), host.call('loadSettings', {})]);
  if (booted.status === 'rejected') {
    /* Le suivi n’est pas joignable : on ne laisse pas croire qu’il tourne. */
    selectedDay = selectedDay ?? dateKey(new Date());
    document.body.dataset.tracking = 'error';
    swapTextAt('#tracking-state', 'Suivi indisponible');
    swapTextAt('#tracking-label', 'Aucune information du suivi');
    $('#tracking-title').title = 'Suivi indisponible';
    booting();
    render();
    reportHostError(booted.reason);
    return;
  }
  const data = booted.value;
  if (loaded.status === 'fulfilled') {
    applySettings(loaded.value?.settings);
    if (typeof loaded.value?.onboarded === 'boolean') onboarded = loaded.value.onboarded;
  }
  /* bootstrap reste la référence pour l’axe horaire et les tâches fixes. */
  if (Array.isArray(data?.workWindows) && data.workWindows.length) WORK_WINDOWS = data.workWindows.map(([open, close]) => [Number(open), Number(close)]);
  if (Array.isArray(data?.lunch) && data.lunch.length === 2) LUNCH = [Number(data.lunch[0]), Number(data.lunch[1])];
  applyFixedTasks(data?.fixedTasks);
  selectedDay = typeof data?.today === 'string' ? data.today : dateKey(new Date());
  miniMonth = startOfMonth(asDate(selectedDay));
  setConfigured(data?.configured !== false);
  if (typeof data?.onboarded === 'boolean') onboarded = data.onboarded;
  renderTarget();
  renderConnections(data?.connections);
  applyTracking(data?.tracking);
  clearHostError();
  booting();
  render();
  /* Premier démarrage : la configuration guidée prend la place de la grille, sans fenêtre modale.
     Déjà guidé mais dépôt illisible : on repart directement sur la question du dépôt. */
  const known = loaded.status === 'fulfilled' ? loaded.value?.settings : null;
  /* Après le premier démarrage, un dépôt devenu illisible se corrige dans la configuration. */
  if (!onboarded) startOnboarding({ settings: known });
  else if (!configured) openSettings(known);
  await loadVisible();
  /* Signalé après le chargement des journées, qui remet le tiroir d’état à zéro. */
  if (loaded.status === 'rejected') reportHostError(loaded.reason);
  if (!isMini() && settingsState?.checkUpdates !== false) checkUpdate({ manual: false });
})();
