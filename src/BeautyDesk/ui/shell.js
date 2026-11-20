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

  /** Вызывает подписчиков сама страница (не программа) — например, когда подгрузились сторонние виджеты. */
  emit(type, data) { for (const handler of BD.handlers[type] ?? []) handler(data); },

  /**
   * Канал данных от сторонней программы (см. Services/Bridge.cs): обработчик получает { value, updatedAt },
   * где updatedAt — когда программа последний раз обновила данные (мс, как Date.now()).
   */
  bridge(name, handler) { BD.on(`bridge:${name}`, handler); },

  /** Команда сторонней программе по каналу данных. */
  bridgeSend(name, command) { BD.send({ type: 'bridgeSend', name, command }); },

  /** Отписка — для виджетов, которые убирают со стола. */
  off(type, handler) { BD.handlers[type] = (BD.handlers[type] ?? []).filter((h) => h !== handler); },

  /** Прямоугольник элемента в координатах страницы — программа переводит его в координаты экрана. */
  rect(element) {
    const r = element.getBoundingClientRect();
    return { x: r.left, y: r.top, width: r.width, height: r.height };
  },

  /**
   * Иконка трея: картинка, которая пересылает программе всё, что с ней делает мышь.
   * Используется и на панели, и в скрытом разделе под стрелочкой.
   */
  trayIcon(item) {
    const img = document.createElement('img');
    img.className = 'tray-icon';
    img.draggable = false;
    const buttons = ['left', 'middle', 'right'];
    for (const [event, kind] of [['mouseenter', 'enter'], ['mouseleave', 'leave'], ['mousedown', 'down'], ['mouseup', 'up']]) {
      img.addEventListener(event, (e) => BD.send({ type: 'tray', id: item.id, kind, button: buttons[e.button ?? 0], rect: BD.rect(img) }));
    }
    BD.updateTrayIcon(img, item);
    return img;
  },

  updateTrayIcon(img, item) {
    if (item.icon && img.getAttribute('src') !== item.icon) img.setAttribute('src', item.icon);
    if (img.title !== item.title) img.title = item.title;
  },

  /** Виды виджетов: название -> описание. Каждый файл из папки widgets/ добавляет сюда свой. */
  widgetTypes: {},

  /**
   * Регистрирует вид виджета.
   * title — ключ перевода названия; sizes — допустимые размеры в клетках, первый — по умолчанию;
   * create(body, context) рисует виджет и может вернуть { update, resize, destroy }.
   */
  widget(name, definition) { BD.widgetTypes[name] = definition; },

  /** Окно только что показали: короткая анимация появления. */
  appear() {
    const root = document.documentElement;
    root.classList.remove('appear');
    void root.offsetWidth; // перезапуск анимации
    root.classList.add('appear');
  },

  start() {
    BD.on('opened', () => BD.appear());
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
// Ошибки скриптов уходят в журнал программы: окна оболочки без консоли, иначе их никто не увидит.
window.addEventListener('error', (event) => BD.send({ type: 'log', text: `${event.message} (${(event.filename ?? '').split('/').pop()}:${event.lineno})` }));
window.addEventListener('unhandledrejection', (event) => BD.send({ type: 'log', text: `Необработанный отказ: ${event.reason}` }));
// Окна оболочки — не веб-страница: собственное меню по правой кнопке им не нужно.
document.addEventListener('contextmenu', (event) => event.preventDefault());

// Сторонние виджеты из папки пользователя: подключаются один раз, после встроенных.
const loadedUserWidgets = new Set();
function loadUserWidgets(urls) {
  for (const url of urls ?? []) {
    if (loadedUserWidgets.has(url)) continue;
    loadedUserWidgets.add(url);
    const script = document.createElement('script');
    script.src = url;
    script.onload = () => BD.emit('widgetTypesChanged');
    script.onerror = () => BD.send({ type: 'log', text: `Сторонний виджет не загрузился: ${url}` });
    document.body.appendChild(script);
  }
}

function applyState() {
  if (BD.role === 'desktop') loadUserWidgets(BD.state.userWidgets);
  const state = BD.state;
  document.documentElement.lang = state.language;
  document.documentElement.dataset.theme = state.theme.id;
  document.documentElement.style.setProperty('--bd-bar-height', `${state.barHeight}px`);
  document.documentElement.style.setProperty('--bd-island-width', `${state.islandWidth}px`);
  document.documentElement.style.setProperty('--bd-dock-height', `${state.dockHeight}px`);
  document.documentElement.style.setProperty('--bd-dock-margin', `${state.dockMargin}px`);
  const link = document.getElementById('theme');
  if (link.getAttribute('href') !== state.theme.css) link.setAttribute('href', state.theme.css);

  // Класс paused останавливает анимации, пока открыто полноэкранное приложение.
  document.body.classList.toggle('paused', state.paused);

  for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = BD.t(el.dataset.i18n);
  for (const el of document.querySelectorAll('[data-i18n-title]')) el.title = BD.t(el.dataset.i18nTitle);
  for (const el of document.querySelectorAll('[data-i18n-placeholder]')) el.placeholder = BD.t(el.dataset.i18nPlaceholder);
}
