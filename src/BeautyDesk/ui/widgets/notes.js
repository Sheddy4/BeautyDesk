// Виджет «Заметка»: просто текст, который всегда под рукой. Сохраняется сам.
BD.widget('notes', {
  title: 'widget.notes',
  sizes: [[4, 3], [4, 2], [2, 2]],
  create(body, context) {
    const area = document.createElement('textarea');
    area.className = 'notes-text';
    area.spellcheck = false;
    area.value = context.options.text ?? '';
    body.appendChild(area);

    // Окно виджетов обычно не берёт клавиатуру — просим её у программы, когда человек собирается печатать.
    area.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));
    let timer = null;
    area.addEventListener('input', () => {
      clearTimeout(timer);
      timer = setTimeout(() => context.setOptions({ text: area.value }), 500);
    });

    const update = () => { area.placeholder = BD.t('widget.notes.placeholder'); };
    update();
    return {
      update,
      destroy() {
        clearTimeout(timer);
        context.setOptions({ text: area.value });
      },
    };
  },
});
