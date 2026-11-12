(function () {
  'use strict';

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.top = '-9999px';
      document.body.appendChild(area);
      area.select();
      try {
        document.execCommand('copy') ? resolve() : reject(new Error('copy failed'));
      } catch (err) {
        reject(err);
      } finally {
        document.body.removeChild(area);
      }
    });
  }

  function decorate() {
    document.querySelectorAll('.markdown-section pre').forEach(function (pre) {
      if (pre.querySelector('.code-copy-btn')) return;
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'code-copy-btn';
      button.textContent = '复制';
      button.setAttribute('aria-label', '复制代码');
      button.addEventListener('click', function () {
        var code = pre.querySelector('code');
        var text = code ? code.innerText : pre.innerText;
        copyText(text).then(function () {
          button.textContent = '已复制';
          button.classList.add('copied');
        }).catch(function () {
          button.textContent = '复制失败';
        }).finally(function () {
          window.setTimeout(function () {
            button.textContent = '复制';
            button.classList.remove('copied');
          }, 1600);
        });
      });
      pre.appendChild(button);
    });
  }

  // Docsify resolves relative image paths against the current docs route,
  // which breaks assets when a page lives in a subdirectory. Normalize any
  // ".../assets/img/..." URL back to the site root.
  function fixImages() {
    document.querySelectorAll('.markdown-section img').forEach(function (img) {
      var src = img.getAttribute('src') || '';
      var marker = '/assets/img/';
      var index = src.indexOf(marker);
      if (index !== -1) {
        img.setAttribute('src', src.slice(index + 1));
      }
    });
  }

  var scheduled = false;
  function schedule() {
    if (scheduled) return;
    scheduled = true;
    window.requestAnimationFrame(function () {
      scheduled = false;
      decorate();
      fixImages();
      // Docsify may rewrite relative image URLs after doneEach; run again.
      window.setTimeout(function () {
        decorate();
        fixImages();
      }, 80);
      window.setTimeout(fixImages, 400);
    });
  }

  function closeSearch() {
    var search = document.querySelector('.sidebar .search');
    if (!search) return;
    var input = search.querySelector('input');
    if (input) input.value = '';
    var panel = search.querySelector('.results-panel');
    if (panel) panel.style.display = 'none';
  }

  document.addEventListener('click', function (event) {
    var link = event.target && event.target.closest
      ? event.target.closest('.sidebar .search .results-panel a')
      : null;
    if (link) window.setTimeout(closeSearch, 120);
  }, true);

  if (window.$docsify) {
    window.$docsify.plugins = (window.$docsify.plugins || []).concat(function (hook) {
      hook.doneEach(schedule);
    });
  } else {
    document.addEventListener('DOMContentLoaded', schedule);
  }
})();
