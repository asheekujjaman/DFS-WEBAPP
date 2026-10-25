(function () {
  var box = document.getElementById('cards');
  if (!box) return;
  var url = box.getAttribute('data-status-url');
  var text = { up: 'Up', degraded: 'Degraded', down: 'Down' };
  async function refresh() {
    try {
      var r = await fetch(url, { credentials: 'same-origin' });
      if (!r.ok) return;
      var list = await r.json();
      list.forEach(function (s) {
        var el = document.getElementById('st-' + s.id);
        if (!el) return;
        el.className = 'st ' + s.state;
        el.querySelector('em').textContent = (text[s.state] || s.state) + (s.state === 'down' ? '' : ' (' + s.ms + ' ms)');
      });
    } catch (e) { }
  }
  refresh();
  setInterval(refresh, 30000);
})();
