"use strict";
(() => {
  // Language links remain ordinary, shareable pages. Storage is optional.
  for (const link of document.querySelectorAll('.language-switch a')) {
    link.addEventListener('click', () => {
      try { localStorage.setItem('mani-site-language', link.dataset.language); } catch {}
    });
  }
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  const panels = [...document.querySelectorAll('[role="tabpanel"]')];
  function activateTab(tab, focus = false) {
    tabs.forEach(t => { t.setAttribute('aria-selected', String(t === tab)); t.tabIndex = t === tab ? 0 : -1; });
    panels.forEach(p => { p.hidden = p.id !== tab.getAttribute('aria-controls'); });
    document.querySelector('.shot-number').textContent = `0${tabs.indexOf(tab) + 1} / 03`;
    if (focus) tab.focus();
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => activateTab(tab));
    tab.addEventListener('keydown', event => {
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next !== undefined) { event.preventDefault(); activateTab(tabs[next], true); }
    });
  });
  const lightbox = document.querySelector('#lightbox');
  document.querySelectorAll('.shot-button').forEach(button => {
    button.addEventListener('click', () => {
      const image = button.querySelector('img');
      lightbox.querySelector('img').src = image.src;
      lightbox.querySelector('img').alt = image.alt;
      lightbox.querySelector('p').textContent = image.alt;
      lightbox.showModal();
    });
  });
  document.querySelectorAll('[data-dialog]').forEach(button => button.addEventListener('click', () => document.getElementById(button.dataset.dialog).showModal()));
  document.querySelectorAll('dialog').forEach(dialog => {
    dialog.querySelector('[data-close]').addEventListener('click', () => dialog.close());
    dialog.addEventListener('click', event => {
      const r = dialog.getBoundingClientRect();
      if (event.target === dialog && (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom)) dialog.close();
    });
  });
  const copy = document.querySelector('[data-copy]');
  copy.addEventListener('click', async () => {
    const feedback = document.querySelector('.copy-feedback');
    try { await navigator.clipboard.writeText(document.querySelector('#checksum').textContent); feedback.textContent = copy.dataset.success; }
    catch { feedback.textContent = copy.dataset.failure; }
  });
})();
