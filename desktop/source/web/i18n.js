import {uz} from './uz.js';

export const language = typeof localStorage !== 'undefined' && localStorage.getItem('kasa-language') === 'uz' ? 'uz' : 'ru';

export function t(text) {
  if (language !== 'uz') return text;
  const trimmed = text.trim();
  if (!trimmed) return text;
  const translated = Object.prototype.hasOwnProperty.call(uz, trimmed) ? uz[trimmed] : null;
  if (translated === null) return text;
  const start = text.indexOf(trimmed);
  return text.slice(0, start) + translated + text.slice(start + trimmed.length);
}

if (language === 'uz' && typeof document !== 'undefined') {
  document.documentElement.lang = 'uz-Latn';
  const walker = document.createTreeWalker(document.documentElement, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (!node.parentElement?.closest('script,style,[translate="no"]'))
      node.data = t(node.data);
  }
  for (const node of document.querySelectorAll('[aria-label],[placeholder],[title]'))
    if (!node.closest('[translate="no"]'))
      for (const attr of ['aria-label', 'placeholder', 'title'])
        if (node.hasAttribute(attr)) node.setAttribute(attr, t(node.getAttribute(attr)));
}
