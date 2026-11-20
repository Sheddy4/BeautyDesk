// Виджет «Фоторамка»: слайд-шоу из выбранной папки. Картинки меняются плавной сменой раз в 20 секунд,
// клик — следующая. Программа отдаёт уменьшенные копии, оригиналы не трогаются.
BD.widget('photos', {
  title: 'widget.photos',
  sizes: [[4, 3], [4, 2], [2, 2], [4, 4]],
  editable: true,
  create(body, context) {
    const INTERVAL = 20000;
    body.innerHTML = '<div class="photo-stage"><img class="photo a" alt=""><img class="photo b" alt=""></div><div class="photo-empty"></div>';
    const [first, second] = body.querySelectorAll('.photo');
    const empty = body.querySelector('.photo-empty');
    let files = [];
    let index = Number(context.options.index ?? 0);
    let front = first;
    let timer = null;
    let loading = false;

    async function choose() {
      const picked = await BD.request('pickPath', { kind: 'folder' }, 'picked');
      if (!picked?.path) return;
      context.setOptions({ folder: picked.path, index: '0' });
      index = 0;
      await load();
    }

    async function load() {
      const folder = context.options.folder;
      empty.hidden = Boolean(folder);
      if (!folder) { renderEmpty(); return; }
      const answer = await BD.request('photoList', { folder }, 'photoList');
      files = answer.files ?? [];
      if (!files.length) { empty.hidden = false; empty.textContent = BD.t('widget.photos.none'); return; }
      await show(index % files.length);
    }

    /** Новая картинка грузится в скрытый слой и проявляется поверх старой. */
    async function show(i) {
      if (loading || !files.length) return;
      loading = true;
      index = (i + files.length) % files.length;
      const width = Math.round(body.clientWidth * (window.devicePixelRatio || 1));
      const answer = await BD.request('photo', { path: files[index], width: Math.max(320, width) }, 'photo');
      loading = false;
      if (!answer.url) { if (files.length > 1) setTimeout(() => show(index + 1), 0); return; }
      const back = front === first ? second : first;
      back.onload = () => { back.classList.add('shown'); front.classList.remove('shown'); front = back; };
      back.src = answer.url;
      context.setOptions({ index: String(index) });
    }

    function renderEmpty() {
      empty.innerHTML = '';
      const button = document.createElement('button');
      button.className = 'plain';
      button.textContent = BD.t('widget.photos.choose');
      button.addEventListener('click', choose);
      empty.appendChild(button);
    }

    body.querySelector('.photo-stage').addEventListener('click', () => show(index + 1));
    timer = setInterval(() => { if (!BD.state?.paused) show(index + 1); }, INTERVAL);
    load();

    let language = BD.state?.language;
    return {
      update() { if (BD.state?.language !== language) { language = BD.state?.language; if (!context.options.folder) renderEmpty(); } },
      edit: choose,
      destroy() { clearInterval(timer); },
    };
  },
});
