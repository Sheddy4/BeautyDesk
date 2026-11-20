// Виджет «Буфер обмена»: последние скопированные тексты. Клик — скопировать снова.
// История хранится только в памяти программы и только пока виджет стоит на столе.
BD.widget('clipboard', {
  title: 'widget.clipboard',
  sizes: [[4, 3], [4, 4], [4, 2]],
  create(body) {
    body.innerHTML = '<div class="clip-head"><span class="muted"></span><button class="plain clip-clear"></button></div><div class="clip-list"></div>';
    const list = body.querySelector('.clip-list');
    let items = [];

    body.querySelector('.clip-clear').addEventListener('click', () => BD.send({ type: 'clipboardClear' }));

    function render() {
      body.querySelector('.clip-head .muted').textContent = BD.t('widget.clipboard.hint');
      body.querySelector('.clip-clear').textContent = BD.t('widget.clipboard.clear');
      body.querySelector('.clip-clear').hidden = items.length === 0;
      list.replaceChildren(...items.map((text, index) => {
        const el = document.createElement('button');
        el.className = 'clip-item';
        el.textContent = text.replace(/\s+/g, ' ').trim();
        el.title = text.length > 300 ? `${text.slice(0, 300)}…` : text;
        el.addEventListener('click', () => {
          BD.send({ type: 'clipboardCopy', index });
          el.classList.add('copied');
        });
        return el;
      }));
      if (!items.length) {
        const empty = document.createElement('div');
        empty.className = 'muted clip-empty';
        empty.textContent = BD.t('widget.clipboard.empty');
        list.appendChild(empty);
      }
    }

    const onData = (data) => { items = data.items; render(); };
    BD.on('clipboard', onData);
    render();
    let language = BD.state?.language;
    return {
      update() { if (BD.state?.language !== language) { language = BD.state?.language; render(); } },
      destroy() { BD.off('clipboard', onData); },
    };
  },
});
