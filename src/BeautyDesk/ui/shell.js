// Общая часть для всех окон оболочки: связь с программой, тема, язык.
// Остальные скрипты (bar.js, island.js, menu.js) подписываются на данные через BD.on.
const BD = {
  role: new URLSearchParams(location.search).get('role'),
  state: null,
  handlers: {},

  send(message) { chrome.webview.postMessage(message); },

  /** Перевод по ключу на текущий язык. */
  t(key) { return BD.state?.strings?.[key] ?? key; },

  /** Подписка: 'state' — тема и язык, остальные названия — темы данных от программы (sys, tray, media, menu). */
  on(type, handler) { (BD.handlers[type] ??= []).push(handler); },

  /** Прямоугольник элемента в координатах страницы — программа переводит его в координаты экрана. */
  rect(element) {
    const r = element.getBoundingClientRect();
    return { x: r.left, y: r.top, width: r.width, height: r.height };
  },

  start() {
    chrome.webview.addEventListener('message', (event) => {
      const message = event.data;
      if (message?.type === 'state') {
        BD.state = message;
        applyState();
      }
      for (const handler of BD.handlers[message?.type] ?? []) handler(message.type === 'state' ? message : message.data);
    });
    BD.send({ type: 'ready' });
  },
};

document.documentElement.dataset.role = BD.role;
// Окна оболочки — не веб-страница: собственное меню по правой кнопке им не нужно.
document.addEventListener('contextmenu', (event) => event.preventDefault());

function applyState() {
  const state = BD.state;
  document.documentElement.lang = state.language;
  document.documentElement.dataset.theme = state.theme.id;
  const link = document.getElementById('theme');
  if (link.getAttribute('href') !== state.theme.css) link.setAttribute('href', state.theme.css);

  // Класс paused останавливает анимации, пока открыто полноэкранное приложение.
  document.body.classList.toggle('paused', state.paused);

  for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = BD.t(el.dataset.i18n);
  for (const el of document.querySelectorAll('[data-i18n-title]')) el.title = BD.t(el.dataset.i18nTitle);
}
