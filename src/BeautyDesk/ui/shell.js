// Страница одна на оба окна; какое из них показывать, решает параметр role в адресе.
const role = new URLSearchParams(location.search).get('role');
document.documentElement.dataset.role = role;

let state = null;

const send = (message) => chrome.webview.postMessage(message);
const t = (key) => state?.strings?.[key] ?? key;

function render() {
  document.documentElement.lang = state.language;
  document.documentElement.dataset.theme = state.theme.id;
  const link = document.getElementById('theme');
  if (link.getAttribute('href') !== state.theme.css) link.setAttribute('href', state.theme.css);

  // Класс paused останавливает анимации, пока открыто полноэкранное приложение.
  document.body.classList.toggle('paused', state.paused);

  for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
  document.getElementById('monitor').textContent = state.monitor;
  document.getElementById('theme-name').textContent = state.themes.find((x) => x.id === state.theme.id)?.name ?? state.theme.id;
  document.getElementById('language-name').textContent = state.language.toUpperCase();
}

const next = (list, current) => list[(list.indexOf(current) + 1) % list.length];

document.getElementById('theme-button').addEventListener('click', () => {
  send({ type: 'setTheme', id: next(state.themes.map((x) => x.id), state.theme.id) });
});
document.getElementById('language-button').addEventListener('click', () => {
  send({ type: 'setLanguage', language: next(state.languages, state.language) });
});
document.getElementById('exit-button').addEventListener('click', () => send({ type: 'exit' }));

chrome.webview.addEventListener('message', (event) => {
  if (event.data?.type === 'state') {
    state = event.data;
    render();
  }
});

send({ type: 'ready' });
