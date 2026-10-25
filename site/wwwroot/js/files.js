(function () {
  var app = document.getElementById('app');
  if (!app) return;
  var d = app.dataset;
  var token = document.querySelector('input[name="__RequestVerificationToken"]').value;
  var log = document.getElementById('log');
  var toast = document.getElementById('toast');

  function q(id) { return document.getElementById(id); }
  function post(url, data) {
    var body = new URLSearchParams(data);
    return fetch(url, { method: 'POST', credentials: 'same-origin', body: body,
      headers: { 'RequestVerificationToken': token, 'Content-Type': 'application/x-www-form-urlencoded' } })
      .then(function (r) { return r.json(); });
  }
  function showToast(msg, undoId) {
    toast.innerHTML = '';
    var s = document.createElement('span'); s.textContent = msg; toast.appendChild(s);
    if (undoId) {
      var b = document.createElement('button'); b.textContent = 'Undo';
      b.addEventListener('click', function () {
        post(d.undoUrl, { loc: d.loc, id: undoId }).then(function (r) {
          if (r.ok) location.reload(); else { s.textContent = r.error || 'Undo failed'; b.remove(); }
        });
      });
      toast.appendChild(b);
    }
    toast.className = 'toast show';
    if (!undoId) setTimeout(function () { toast.className = 'toast'; }, 5000);
  }

  // search
  var search = q('search');
  if (search) search.addEventListener('input', function (e) {
    var t = e.target.value.toLowerCase();
    document.querySelectorAll('tr.item').forEach(function (r) { r.style.display = r.textContent.toLowerCase().indexOf(t) >= 0 ? '' : 'none'; });
  });

  // buttons
  var nfBtn = q('nfBtn'); if (nfBtn) nfBtn.addEventListener('click', function () { q('nf').classList.toggle('open'); });
  var fileInput = q('fileInput'), folderInput = q('folderInput');
  var upBtn = q('upBtn'); if (upBtn) upBtn.addEventListener('click', function () { fileInput.click(); });
  var upFolderBtn = q('upFolderBtn'); if (upFolderBtn) upFolderBtn.addEventListener('click', function () { folderInput.click(); });

  // ---- upload ----
  function sendOne(file, rel, row) {
    return new Promise(function (resolve) {
      var xhr = new XMLHttpRequest();
      xhr.open('POST', d.uploadUrl);
      xhr.setRequestHeader('RequestVerificationToken', token);
      xhr.upload.onprogress = function (ev) {
        if (ev.lengthComputable) row.querySelector('i').style.width = Math.round(ev.loaded / ev.total * 100) + '%';
      };
      xhr.onload = function () {
        var ok = false, err = 'Upload failed (' + xhr.status + ')';
        try { var r = JSON.parse(xhr.responseText); ok = r.ok; if (r.error) err = r.error; } catch (e) { }
        row.className = 'up ' + (ok ? 'ok' : 'bad');
        row.querySelector('span').textContent = (ok ? 'Done: ' : 'Failed: ') + rel + (ok ? '' : ' - ' + err);
        resolve(ok);
      };
      xhr.onerror = function () { row.className = 'up bad'; row.querySelector('span').textContent = 'Failed: ' + rel + ' - network error'; resolve(false); };
      var fd = new FormData();
      fd.append('loc', d.loc); fd.append('path', d.path); fd.append('relativePath', rel); fd.append('file', file);
      xhr.send(fd);
    });
  }
  async function start(files) {
    var allOk = true;
    for (var f of Array.from(files)) {
      var rel = f.webkitRelativePath || f.name;
      var row = document.createElement('div');
      row.className = 'up';
      row.innerHTML = '<span></span><div class="bar"><i></i></div>';
      row.querySelector('span').textContent = 'Uploading: ' + rel;
      log.appendChild(row);
      if (!(await sendOne(f, rel, row))) allOk = false;
    }
    if (allOk) location.reload();
    else { var a = document.createElement('a'); a.className = 'chip'; a.href = location.href; a.textContent = 'Refresh list'; log.appendChild(a); }
  }
  if (fileInput) fileInput.addEventListener('change', function (e) { start(e.target.files); });
  if (folderInput) folderInput.addEventListener('change', function (e) { start(e.target.files); });

  // ---- delete dialog (folders need the name typed) ----
  var delDlg = q('delDlg'), delPath = '', delName = '', delIsDir = false;
  document.querySelectorAll('button[data-del]').forEach(function (b) {
    b.addEventListener('click', async function () {
      delPath = b.dataset.del; delName = b.dataset.name; delIsDir = b.dataset.dir === '1';
      q('delTitle').textContent = 'Delete "' + delName + '"?';
      q('delInfo').textContent = d.mode === 'Permanent' ? 'This will delete it permanently.' : 'It will be moved to the recycle bin. You can undo right after.';
      var typeBox = q('delTypeBox'), typeIn = q('delType'), ok = q('delOk');
      typeIn.value = '';
      typeBox.hidden = !delIsDir;
      ok.disabled = delIsDir;
      if (delIsDir) {
        q('delCount').textContent = 'Checking folder contents...';
        try {
          var r = await fetch(d.countUrl + '?loc=' + encodeURIComponent(d.loc) + '&path=' + encodeURIComponent(delPath), { credentials: 'same-origin' });
          var c = await r.json();
          q('delCount').textContent = 'This folder contains ' + c.files + (c.truncated ? '+' : '') + ' file(s) and ' + c.folders + (c.truncated ? '+' : '') + ' folder(s). Type the folder name to confirm:';
        } catch (e) { q('delCount').textContent = 'Type the folder name to confirm:'; }
      }
      delDlg.showModal();
    });
  });
  var delType = q('delType');
  if (delType) delType.addEventListener('input', function () { q('delOk').disabled = delIsDir && delType.value.trim() !== delName; });
  if (q('delCancel')) q('delCancel').addEventListener('click', function () { delDlg.close(); });
  if (q('delOk')) q('delOk').addEventListener('click', function () {
    q('delOk').disabled = true;
    post(d.deleteUrl, { loc: d.loc, path: delPath }).then(function (r) {
      delDlg.close();
      if (r.ok) {
        document.querySelectorAll('button[data-del]').forEach(function (b) { if (b.dataset.del === delPath) b.closest('tr').remove(); });
        showToast('"' + delName + '" deleted.', r.undoId);
      } else showToast(r.error || 'Delete failed');
    });
  });

  // ---- preview dialog ----
  var vDlg = q('viewDlg');
  document.querySelectorAll('button[data-view]').forEach(function (b) {
    b.addEventListener('click', function () {
      var url = d.previewUrl + '?loc=' + encodeURIComponent(d.loc) + '&path=' + encodeURIComponent(b.dataset.view);
      q('viewTitle').textContent = b.dataset.name;
      var frame = q('viewFrame'), box = q('viewImg');
      if (b.dataset.kind === 'img') { frame.hidden = true; box.hidden = false; box.innerHTML = ''; var im = document.createElement('img'); im.src = url; im.alt = b.dataset.name; box.appendChild(im); }
      else { box.hidden = true; frame.hidden = false; frame.src = url; }
      q('viewDownload').href = d.downloadUrl + '?loc=' + encodeURIComponent(d.loc) + '&path=' + encodeURIComponent(b.dataset.view);
      vDlg.showModal();
    });
  });
  if (q('viewClose')) q('viewClose').addEventListener('click', function () { vDlg.close(); });
  if (vDlg) vDlg.addEventListener('close', function () { q('viewFrame').src = 'about:blank'; q('viewImg').innerHTML = ''; });
})();
