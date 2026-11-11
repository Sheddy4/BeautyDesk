// Выпадающее меню. Пункты присылает программа; страница только рисует их и сообщает о выборе.
if (BD.role === 'menu') {
  const menu = document.getElementById('menu');

  BD.on('menu', (data) => {
    menu.replaceChildren();
    for (const item of data.items) {
      if (item.separator) {
        menu.appendChild(document.createElement('hr'));
        continue;
      }
      const button = document.createElement('button');
      button.className = 'menu-item';
      button.textContent = item.label;
      button.addEventListener('click', () => {
        // Опасные пункты (перезагрузка, выключение) срабатывают только со второго нажатия.
        if (item.confirm && !button.classList.contains('armed')) {
          button.classList.add('armed');
          button.textContent = `${item.label} — ${BD.t('menu.confirm').toLowerCase()}`;
          return;
        }
        BD.send({ type: 'menuAction', id: item.id });
      });
      menu.appendChild(button);
    }
  });
}
