// Виджет «Монитор железа»: загрузка процессора, видеокарты и памяти, температура видеокарты.
BD.widget('hardware', {
  title: 'widget.hardware',
  sizes: [[4, 2], [2, 2]],
  create(body) {
    const rows = {};
    for (const name of ['cpu', 'gpu', 'memory']) {
      const row = document.createElement('div');
      row.className = 'hw-row';
      row.innerHTML = '<span class="hw-label"></span><span class="hw-track"><i></i></span><span class="hw-value"></span>';
      body.appendChild(row);
      rows[name] = row;
    }
    let data = null;

    function set(name, label, percent, text) {
      const row = rows[name];
      row.hidden = percent === null || percent === undefined;
      if (row.hidden) return;
      row.querySelector('.hw-label').textContent = label;
      row.querySelector('i').style.width = `${percent}%`;
      // Высокая загрузка подсвечивается.
      row.classList.toggle('high', percent >= 85);
      row.querySelector('.hw-value').textContent = text;
    }

    function render() {
      if (!data) {
        set('cpu', BD.t('widget.hardware.cpu'), 0, '—');
        set('gpu', null, null);
        set('memory', BD.t('widget.hardware.memory'), 0, '—');
        return;
      }
      set('cpu', BD.t('widget.hardware.cpu'), data.cpu, `${data.cpu}%`);
      const temperature = data.gpuTemperature === null ? '' : ` · ${data.gpuTemperature}°`;
      set('gpu', BD.t('widget.hardware.gpu'), data.gpu, `${data.gpu}%${temperature}`);
      set('memory', BD.t('widget.hardware.memory'), data.memory, `${data.memoryUsedGb} / ${data.memoryTotalGb} ${BD.t('widget.hardware.gb')}`);
      rows.gpu.title = data.gpuName ?? '';
    }

    const onData = (snapshot) => { data = snapshot; render(); };
    BD.on('hw', onData);
    render();

    return {
      update: render,
      destroy() { BD.off('hw', onData); },
    };
  },
});
