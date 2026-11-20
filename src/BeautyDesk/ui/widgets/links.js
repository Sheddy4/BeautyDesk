// Виджет «Ярлыки»: плитки с папками, файлами и сайтами, которые человек добавляет сам.
// Добавление — в режиме расстановки кнопкой с карандашом; там же плитки убираются.
(() => {
  BD.widget('links', {
    title: 'widget.links',
    sizes: [[4, 2], [2, 2], [4, 3]],
    editable: true,
    create(body, context) {
      let editing = false;
      const read = () => { try { return JSON.parse(context.options.items ?? '[]'); } catch { return []; } };
      const save = (items) => context.setOptions({ items: JSON.stringify(items) });

      async function addPath(kind) {
        const picked = await BD.request('pickPath', { kind }, 'picked');
        if (!picked?.path) return;
        save([...read(), { target: picked.path, name: picked.name, icon: picked.icon }]);
        render();
      }

      function addSite(input) {
        let url = input.value.trim();
        if (!url) return;
        if (!/^https?:\/\//i.test(url)) url = `https://${url}`;
        let name = url;
        try { name = new URL(url).hostname.replace(/^www\./, ''); } catch { return; }
        save([...read(), { target: url, name, site: true }]);
        input.value = '';
        render();
      }

      function render() {
        const items = read();
        body.replaceChildren();
        const grid = document.createElement('div');
        grid.className = 'links-grid';
        items.forEach((item, index) => {
          const tile = document.createElement('button');
          tile.className = 'link-tile';
          tile.title = item.target;
          const icon = item.icon ? `<img src="${item.icon}" alt="">` : `<span class="link-letter">${(item.name || '?').charAt(0).toUpperCase()}</span>`;
          tile.innerHTML = `${icon}<span class="link-name"></span>`;
          tile.querySelector('.link-name').textContent = item.name;
          if (editing) {
            const remove = document.createElement('span');
            remove.className = 'link-remove';
            remove.textContent = '✕';
            tile.appendChild(remove);
            tile.addEventListener('click', () => { const next = read(); next.splice(index, 1); save(next); render(); });
          } else {
            tile.addEventListener('click', () => BD.send({ type: 'openLink', target: item.target }));
          }
          grid.appendChild(tile);
        });
        body.appendChild(grid);

        if (editing || items.length === 0) {
          const form = document.createElement('div');
          form.className = 'links-form';
          form.innerHTML = '<button class="plain" data-kind="folder"></button><button class="plain" data-kind="file"></button><input type="text" spellcheck="false"><button class="plain links-done"></button>';
          form.querySelector('[data-kind="folder"]').textContent = BD.t('widget.links.folder');
          form.querySelector('[data-kind="file"]').textContent = BD.t('widget.links.file');
          const input = form.querySelector('input');
          input.placeholder = BD.t('widget.links.site');
          input.addEventListener('pointerdown', () => BD.send({ type: 'focus' }));
          input.addEventListener('keydown', (event) => { if (event.key === 'Enter') addSite(input); });
          for (const b of form.querySelectorAll('[data-kind]')) b.addEventListener('click', () => addPath(b.dataset.kind));
          const done = form.querySelector('.links-done');
          done.textContent = BD.t('widget.links.done');
          done.hidden = !editing;
          done.addEventListener('click', () => { editing = false; body.classList.remove('configuring'); render(); });
          body.appendChild(form);
        }
        body.classList.toggle('configuring', editing || items.length === 0);
      }

      render();
      let language = BD.state?.language;
      return {
        update() { if (BD.state?.language !== language) { language = BD.state?.language; render(); } },
        edit() { editing = true; render(); },
      };
    },
  });
})();
