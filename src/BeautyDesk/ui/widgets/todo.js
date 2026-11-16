// Виджет «Список дел»: добавить дело, отметить сделанным, убрать.
BD.widget('todo', {
  title: 'widget.todo',
  sizes: [[4, 3], [4, 4], [4, 2]],
  create(body, context) {
    body.innerHTML = '<div class="todo-list"></div><input class="todo-input" type="text" spellcheck="false">';
    const list = body.querySelector('.todo-list');
    const input = body.querySelector('.todo-input');

    // Дела хранятся в настройках виджета одной строкой JSON: [{ text, done }].
    let items = [];
    try { items = JSON.parse(context.options.items ?? '[]'); } catch { items = []; }
    const save = () => context.setOptions({ items: JSON.stringify(items) });

    function render() {
      list.replaceChildren();
      items.forEach((item, index) => {
        const row = document.createElement('div');
        row.className = 'todo-item';
        row.classList.toggle('done', item.done);

        const check = document.createElement('span');
        check.className = 'todo-check';
        const text = document.createElement('span');
        text.className = 'todo-text';
        text.textContent = item.text;
        const remove = document.createElement('button');
        remove.className = 'todo-remove';
        remove.textContent = '✕';

        // Клик по строке отмечает дело сделанным (или снимает отметку), крестик — убирает.
        row.addEventListener('click', () => { item.done = !item.done; save(); render(); });
        remove.addEventListener('click', (event) => { event.stopPropagation(); items.splice(index, 1); save(); render(); });
        row.append(check, text, remove);
        list.appendChild(row);
      });
      input.placeholder = BD.t('widget.todo.placeholder');
    }

    input.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));
    input.addEventListener('keydown', (event) => {
      const text = input.value.trim();
      if (event.key !== 'Enter' || !text) return;
      items.push({ text, done: false });
      input.value = '';
      save();
      render();
      list.scrollTop = list.scrollHeight;
    });

    render();
    return { update() { input.placeholder = BD.t('widget.todo.placeholder'); } };
  },
});
