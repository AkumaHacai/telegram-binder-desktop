'use strict';

  const COMMAND_RE = /^[/.!][\p{L}\p{N}_-]{1,32}$/u;
  const TOKEN_RE = /(?:^|[\s\u00a0])([/.!][\p{L}\p{N}_-]{0,32})$/u;
  const MAX_TEMPLATES = 300;
  const MAX_VARIANTS = 16;
  const MAX_LENGTH = 20000;
  const MAX_FOLDERS = 50;
  const newId = () => typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID() : `tb-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const normalizedCommand = (value) => String(value || '').trim().toLowerCase();
  const commandName = (command) => command.slice(1).toLowerCase();
  const esc = (value) => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  const plainContent = (value) => ({ text: String(value || '').replace(/\r\n?/g, '\n'), html: '' });
  const safeUrl = (value) => {
    try {
      const parsed = new URL(value);
      return /^(https?:|mailto:|tg:)$/.test(parsed.protocol) ? parsed.href : '';
    } catch { return ''; }
  };

  function extractEmojiId(element) {
    const raw = element.getAttribute('data-document-id')
      || element.getAttribute('emoji-id')
      || element.getAttribute('data-emoji-id')
      || element.getAttribute('data-custom-emoji-id')
      || element.getAttribute('data-doc-id');
    return raw && /^\d{10,24}$/.test(raw) ? raw : '';
  }

  function captureRich(root) {
    const quoteSelector = 'blockquote, .quote-block, [data-chat-input-blockquote], [data-rich-message-blockquote]';
    const keepBlocks = !!root.querySelector(`${quoteSelector}, ol, ul`);
    const empty = { text: '', html: '' };

    function join(parts) {
      let text = '';
      let html = '';
      const marks = [];
      let previous;
      for (const part of parts) {
        if (!part.text && !part.html) continue;
        if (previous && (previous.block || part.block)) text += '\n';
        for (const mark of part.marks || []) marks.push({ ...mark, offset: mark.offset + text.length });
        text += part.text;
        html += part.html;
        previous = part;
      }
      return { text, html, marks };
    }

    const childrenOf = (node) => join([...node.childNodes].map(walk));
    const walk = (node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        const text = node.nodeValue || '';
        return { text, html: esc(text) };
      }
      if (node.nodeType === Node.DOCUMENT_FRAGMENT_NODE) return childrenOf(node);
      if (node.nodeType !== Node.ELEMENT_NODE) return empty;
      const el = node;
      const tag = el.tagName.toLowerCase();
      if (el.matches('script, style, svg, canvas, button, .placeholder, .ProseMirror-trailingBreak, .ProseMirror-separator, .chat-input-trailing-placeholder, .chat-input-context-placeholder-label')) return empty;
      const emojiId = extractEmojiId(el);
      if (el.getAttribute('aria-hidden') === 'true' && !emojiId) return empty;
      if (emojiId) {
        const fallback = (el.getAttribute('alt') || el.getAttribute('data-alt')
          || el.getAttribute('data-emoji') || el.textContent || '🙂').trim().slice(0, 16) || '🙂';
        return marked({ text: fallback, html: `<img alt="${esc(fallback)}" data-document-id="${emojiId}">` }, 'custom-emoji://' + emojiId);
      }
      if (tag === 'img') {
        const fallback = el.getAttribute('alt') || '';
        return { text: fallback, html: esc(fallback) };
      }
      if (tag === 'br') return { text: '\n', html: '<br>' };
      if (el !== root && el.matches(quoteSelector)) {
        const body = el.querySelector(':scope > [data-blockquote-content], :scope > [data-rich-copy-wrapper]') || el;
        const part = childrenOf(body);
        const rich = el.hasAttribute('data-rich-message-blockquote') || el.getAttribute('data-chat-input-blockquote-mode') === 'rich';
        const collapsed = el.matches('[data-collapsed="1"], [data-collapsed="true"], [expandable]');
        const attributes = (rich ? ' data-rich-message-blockquote=""' : '') + (collapsed ? ' data-collapsed="1"' : '');
        return marked({ ...part, html: `<blockquote${attributes}>${part.html}</blockquote>`, block: true }, collapsed ? '>^' : '>');
      }
      if (tag === 'ol' || tag === 'ul') {
        const items = [...el.children].filter((child) => child.tagName === 'LI');
        const ordered = tag === 'ol';
        const start = el.hasAttribute('start') ? el.start : el.reversed ? items.length : 1;
        let number = start;
        const part = join(items.map((item) => {
          const part = walk(item);
          if (ordered && item.hasAttribute('value')) number = item.value;
          const marker = ordered ? `${number}. ` : '• ';
          number += el.reversed ? -1 : 1;
          return { ...part, text: marker + part.text, marks: (part.marks || []).map((mark) => ({ ...mark, offset: mark.offset + marker.length })) };
        }));
        const attributes = ordered ? `${el.hasAttribute('start') ? ` start="${start}"` : ''}${el.reversed ? ' reversed' : ''}` : '';
        return { ...part, html: `<${tag}${attributes}>${part.html}</${tag}>`, block: true };
      }
      const children = childrenOf(el);
      let { text, html } = children;
      if (keepBlocks && el.matches('cite, [data-blockquote-caption-content], [data-rich-editor-caption]')) {
        return text.trim() ? { ...children, html: `<cite data-blockquote-caption-content="">${html}</cite>`, block: true } : empty;
      }
      if (el !== root && keepBlocks) {
        if (tag === 'li') return { ...children, html: `<li${el.hasAttribute('value') ? ` value="${el.value}"` : ''}>${html}</li>`, block: true };
        if (tag === 'p' || el.hasAttribute('data-chat-input-paragraph')) return { ...children, html: `<p>${html}</p>`, block: true };
        if (tag === 'div' && !el.matches('[data-chat-input-list-item-content], [data-node-view-content]')) {
          return { ...children, html: `<div>${html}</div>`, block: true };
        }
      }
      const styles = { strong: 'b', b: 'b', em: 'i', i: 'i', u: 'u', s: 's', del: 's', strike: 's', code: 'code', pre: 'pre' };
      let id;
      if (Object.hasOwn(styles, tag) && text) {
        html = `<${styles[tag]}>${html}</${styles[tag]}>`;
        id = { b: '**', i: '__', u: '^^', s: '~~', code: '`', pre: '```' }[styles[tag]];
      } else if (tag === 'a' && text) {
        const href = safeUrl(el.getAttribute('href'));
        if (href) { html = `<a href="${esc(href)}">${html}</a>`; id = href; }
      } else if (el.hasAttribute('data-entity-type')
        && String(el.getAttribute('data-entity-type')).toLowerCase().includes('spoiler')) {
        html = `<span data-entity-type="MessageEntitySpoiler">${html}</span>`;
        id = '||';
      } else if (tag === 'tg-spoiler') {
        html = `<span data-entity-type="MessageEntitySpoiler">${html}</span>`;
        id = '||';
      } else if (!keepBlocks && (tag === 'div' || tag === 'p')) {
        if (el !== root && el.nextSibling && (text || html)) { text += '\n'; html += '<br>'; }
      }
      return marked({ ...children, text, html }, id);
    };
    const result = walk(root);
    result.text = result.text.replace(/\n+$/g, '');
    result.html = result.html.replace(/(?:<br>)+$/g, '');
    if (result.html === esc(result.text).replace(/\n/g, '<br>')) result.html = '';
    return result;
  }

  function normalizeContent(value) {
    const text = typeof value?.text === 'string' ? value.text.replace(/\r\n?/g, '\n') : '';
    if (text.length > MAX_LENGTH) throw new Error(t('tooLong'));
    const content = plainContent(text);
    if (typeof value?.html === 'string' && value.html) {
      if (value.html.length > MAX_LENGTH * 10) throw new Error(t('tooMuchHtml'));
      const clean = parseHtml(value.html);
      if (clean.text !== text) throw new Error(t('htmlMismatch'));
      content.html = clean.html;
    }
    if (value?.desktopTags !== undefined) content.desktopTags = checkedTags(value.desktopTags, text);
    return content;
  }

  function normalizeTemplate(raw) {
    if (!raw || !COMMAND_RE.test(normalizedCommand(raw.command))) throw new Error(t('invalidCommand'));
    const variants = raw.variants;
    if (!Array.isArray(variants) || variants.length === 0 || variants.length > MAX_VARIANTS) {
      throw new Error(t('invalidVariants'));
    }
    return {
      id: typeof raw.id === 'string' && raw.id.length <= 100 ? raw.id : newId(),
      command: normalizedCommand(raw.command),
      description: String(raw.description || '').slice(0, 200),
      folderId: typeof raw.folderId === 'string' ? raw.folderId.slice(0, 100) : '',
      favorite: raw.favorite === true,
      enabled: raw.enabled !== false,
      variants: variants.map((variant) => {
        const content = normalizeContent(variant.content || plainContent(variant.text));
        if (!content.text.trim()) throw new Error(t('emptyVariant'));
        return {
          id: typeof variant.id === 'string' && variant.id.length <= 100 ? variant.id : newId(),
          label: String(variant.label || t('defaultVariant')).slice(0, 80),
          content,
        };
      }),
    };
  }

  function normalizeState(raw) {
    if (!raw || raw.version !== 2 || !Array.isArray(raw.templates)
      || !Array.isArray(raw.folders) || raw.templates.length > MAX_TEMPLATES
      || raw.folders.length > MAX_FOLDERS) throw new Error(t('invalidData'));
    const templateNames = new Set();
    const folderNames = new Set();
    const folders = raw.folders.map((folder) => {
      const label = String(folder.name || '').trim().slice(0, 50);
      if (!label || folderNames.has(label.toLowerCase())) throw new Error(t('invalidFolder'));
      folderNames.add(label.toLowerCase());
      return { id: String(folder.id || newId()).slice(0, 100), name: label };
    });
    const ids = new Set(folders.map((folder) => folder.id));
    const templates = raw.templates.map((value) => {
      const template = normalizeTemplate(value);
      const name = commandName(template.command);
      if (templateNames.has(name)) throw new Error(t('repeatedCommand', { name: template.command }));
      templateNames.add(name);
      if (!ids.has(template.folderId)) template.folderId = '';
      return template;
    });
    return { version: 2, folders, templates };
  }

  function sampleState() {
    return {
      version: 2, folders: [],
      templates: [{ id: 'sample-support', command: '/сап', description: 'Ответ технической поддержки',
        folderId: '', favorite: true, enabled: true,
        variants: [{ id: 'sample-main', label: t('defaultVariant'), content: plainContent(
          'Сейчас поддержка перегружена обращениями по смене IP-адресов. Вы можете дождаться ответа либо закрыть тикет и воспользоваться разделом «Администрирование».') }] }],
    };
  }
  function renderFolders() {
    const selected = ui.filter.value;
    ui.filter.replaceChildren(new Option(t('all'), 'all'), new Option(t('favorites'), 'favorites'),
      ...state.folders.map((f) => new Option(f.name, f.id)));
    ui.filter.value = [...ui.filter.options].some((opt) => opt.value === selected) ? selected : 'all';
    const selection = ui.folder.value;
    ui.folder.replaceChildren(new Option(t('noFolder'), ''), ...state.folders.map((f) => new Option(f.name, f.id)));
    ui.folder.value = [...ui.folder.options].some((opt) => opt.value === selection) ? selection : '';
    const managed = ui.folderManager.value;
    ui.folderManager.replaceChildren(new Option(t('selectFolder'), ''), ...state.folders.map((f) => new Option(f.name, f.id)));
    ui.folderManager.value = state.folders.some((f) => f.id === managed) ? managed : '';
    ui.deleteFolder.disabled = !ui.folderManager.value;
  }

  function getWorkingDefault() {
    return { id: newId(), command: '', description: '', folderId: '', favorite: false, enabled: true,
      variants: [{ id: newId(), label: t('defaultVariant'), content: plainContent('') }] };
  }

  function syncVariant() {
    if (!working) return;
    working.command = normalizedCommand(ui.command.value);
    working.description = ui.description.value.trim();
    working.folderId = ui.folder.value;
    const variant = working.variants[currentVariant];
    if (!variant) return;
    variant.label = ui.variantLabel.value.trim() || t('defaultVariant');
    const newText = ui.message.value.replace(/\r\n?/g, '\n');
    if (variant.content.text !== newText) variant.content = plainContent(newText);
  }

  function renderWorking() {
    if (!working) return;
    ui.command.value = working.command;
    ui.description.value = working.description;
    ui.folder.value = working.folderId;
    ui.variants.replaceChildren(...working.variants.map((variant, index) => new Option(
      `${index + 1}. ${variant.label || t('defaultVariant')}`, String(index))));
    ui.variants.value = String(currentVariant);
    const variant = working.variants[currentVariant];
    ui.variantLabel.value = variant.label;
    ui.message.value = variant.content.text;
    const customCount = (variant.content.desktopTags || desktopTags(variant.content))
      .filter((tag) => tag.id.includes('custom-emoji://')).length;
    ui.richState.textContent = variant.content.html || variant.content.desktopTags?.length
      ? [t('formatting'), customCount && t('emojiCount', { n: customCount })].filter(Boolean).join(' · ')
      : t('plain');
    ui.removeVariant.disabled = working.variants.length === 1;
    ui.formTitle.textContent = editingId ? t('editCommand') : t('newCommand');
    ui.save.textContent = editingId ? t('saveChanges') : t('createCommand');
  }

  function resetForm() {
    editingId = '';
    currentVariant = 0;
    working = getWorkingDefault();
    initialDraft = JSON.stringify(working);
    renderWorking();
  }

  async function editTemplate(template) {
    if (!canDiscardDraft()) return;
    editingId = template?.id || '';
    currentVariant = 0;
    working = template ? structuredClone(template) : getWorkingDefault();
    initialDraft = JSON.stringify(working);
    renderWorking();
    await showEditor(true);
    ui.command.focus();
  }

  async function saveForm(event) {
    event.preventDefault();
    syncVariant();
    if (!COMMAND_RE.test(working.command)) return notify(t('commandRules'), true);
    const name = commandName(working.command);
    if (state.templates.some((t) => t.id !== editingId && commandName(t.command) === name)) return notify(t('duplicateCommand'), true);
    if (working.variants.some((v) => !v.content.text.trim() || v.content.text.length > MAX_LENGTH)) return notify(t('replyLength'), true);
    if (!editingId && state.templates.length >= MAX_TEMPLATES) return notify(t('maxCommands'), true);
    const next = structuredClone(state);
    if (editingId) {
      const index = state.templates.findIndex((t) => t.id === editingId);
      if (index < 0) return notify(t('missingCommand'), true);
      const { favorite, enabled } = state.templates[index];
      next.templates[index] = normalizeTemplate({ ...working, favorite, enabled });
    } else next.templates.push(normalizeTemplate(working));
    await persist(next);
    resetForm();
    await showEditor(false);
    ui.addCommand.focus();
    notify(t('saved'));
  }

  function renderTemplates() {
    if (!ui) return;
    const query = ui.search.value.trim().toLowerCase();
    const nameQuery = query.replace(/^[/.!]/, '');
    const filter = ui.filter.value;
    const templates = state.templates.filter((t) => {
      if (filter === 'favorites' && !t.favorite) return false;
      if (filter !== 'all' && filter !== 'favorites' && t.folderId !== filter) return false;
      return !query || commandName(t.command).includes(nameQuery) || t.description.toLowerCase().includes(query)
        || t.variants.some((variant) => variant.content.text.toLowerCase().includes(query));
    }).sort((a, b) => Number(b.favorite) - Number(a.favorite) || a.command.localeCompare(b.command, locale));
    ui.count.textContent = t('count', { visible: templates.length, total: state.templates.length });
    ui.list.replaceChildren();
    if (!templates.length) {
      const blank = document.createElement('p');
      blank.className = 'empty';
      blank.textContent = t('notFound');
      ui.list.appendChild(blank);
      return;
    }
    for (const template of templates) {
      const row = document.createElement('div');
      row.className = 'bind';
      const description = document.createElement('div');
      description.className = 'bind-content';
      const heading = document.createElement('div');
      heading.className = 'bind-meta';
      const command = document.createElement('code');
      command.className = 'bind-command';
      command.dir = 'ltr';
      command.textContent = template.command;
      const folder = state.folders.find((f) => f.id === template.folderId);
      const extras = document.createElement('span');
      extras.className = 'meta-detail';
      extras.textContent = [folder?.name, template.variants.length > 1 ? t('variantCount', { n: template.variants.length }) : null]
        .filter(Boolean).join(' · ');
      heading.append(command, extras);
      const content = document.createElement('p');
      content.className = 'bind-text';
      content.textContent = template.description || template.variants[0].content.text;
      description.append(heading, content);
      const actions = document.createElement('div');
      actions.className = 'bind-actions';
      const fav = document.createElement('button');
      fav.type = 'button';
      fav.className = 'favorite';
      fav.textContent = template.favorite ? '★' : '☆';
      fav.title = template.favorite ? t('unfavorite') : t('favorite');
      fav.setAttribute('aria-label', fav.title);
      fav.addEventListener('click', action(() => updateTemplate(template.id, { favorite: !template.favorite })));
      const toggle = document.createElement('input');
      toggle.type = 'checkbox';
      toggle.checked = template.enabled;
      toggle.title = t('toggle');
      toggle.setAttribute('aria-label', t('enableCommand', { name: template.command }));
      toggle.addEventListener('change', action(async () => {
        const enabled = toggle.checked;
        toggle.checked = template.enabled;
        await updateTemplate(template.id, { enabled });
      }));
      const edit = document.createElement('button');
      edit.type = 'button';
      edit.className = 'ghost';
      edit.textContent = t('edit');
      edit.addEventListener('click', action(() => editTemplate(template)));
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'danger';
      remove.textContent = t('delete');
      remove.addEventListener('click', action(async () => {
        if (!confirm(t('confirmDelete', { name: template.command }))) return;
        await persist({ ...state, templates: state.templates.filter((t) => t.id !== template.id) });
        if (editingId === template.id) { resetForm(); await showEditor(false); }
      }));
      actions.append(fav, toggle, edit, remove);
      row.append(description, actions);
      ui.list.appendChild(row);
    }
  }

  function renderAll() {
    if (!ui) return;
    renderFolders();
    renderTemplates();
  }

  async function newFolder() {
    const name = prompt(t('folderName'));
    if (name === null) return;
    const clean = name.trim().slice(0, 50);
    if (!clean) return notify(t('emptyFolder'), true);
    if (state.folders.some((folder) => folder.name.toLowerCase() === clean.toLowerCase())) return notify(t('duplicateFolder'), true);
    if (state.folders.length >= MAX_FOLDERS) return notify(t('maxFolders'), true);
    const folder = { id: newId(), name: clean };
    await persist({ ...state, folders: [...state.folders, folder] });
    ui.folderManager.value = folder.id;
    ui.deleteFolder.disabled = false;
    notify(t('folderCreated'));
  }

  async function deleteFolder() {
    const id = ui.folderManager.value;
    const folder = state.folders.find((item) => item.id === id);
    if (!folder) return notify(t('selectFolder'), true);
    if (!confirm(t('confirmDeleteFolder', { name: folder.name }))) return;
    await persist({ ...state,
      folders: state.folders.filter((item) => item.id !== id),
      templates: state.templates.map((template) => template.folderId === id ? { ...template, folderId: '' } : template),
    });
    if (working?.folderId === id) working.folderId = '';
    notify(t('folderDeleted'));
  }

function marked(part, id) {
  if (id && part.text) {
    (part.marks ||= []).push({ offset: 0, length: part.text.length, id });
  }
  return part;
}

function parseHtml(html) {
  // Telegram backups are parsed in an inert template and never inserted into the manager.
  const template = document.createElement('template');
  template.innerHTML = html;
  return captureRich(template.content);
}

const inlineTags = new Map([
  ['**', ['<b>', '</b>']], ['__', ['<i>', '</i>']], ['^^', ['<u>', '</u>']],
  ['~~', ['<s>', '</s>']], ['`', ['<code>', '</code>']], ['```', ['<pre>', '</pre>']],
  ['||', ['<span data-entity-type="MessageEntitySpoiler">', '</span>']],
]);
const emojiTag = /^custom-emoji:\/\/(\d{1,20})(?:\?\d+)?$/;
const isTag = (id) => inlineTags.has(id) || id === '>' || id === '>^'
  || /^```[\w+-]{1,60}$/.test(id) || emojiTag.test(id) || !!safeUrl(id);

function checkedTags(tags, text, capture = false) {
  if (!Array.isArray(tags) || tags.length > text.length) throw new Error(t('invalidTags'));
  let end = 0;
  return tags.map(({ offset, length, id }) => {
    if (!Number.isInteger(offset) || !Number.isInteger(length) || offset < end
      || length <= 0 || offset + length > text.length || typeof id !== 'string' || id.length > 4096) {
      throw new Error(t('invalidTags'));
    }
    end = offset + length;
    const parts = id.split('\\');
    if (!capture && !parts.every(isTag)) throw new Error(t('unknownTag'));
    return { offset, length, id: parts.filter(isTag).join('\\') };
  }).filter((tag) => tag.id);
}

function desktopTags(content) {
  if (content.desktopTags) return content.desktopTags;
  if (!content.html) return [];
  const points = new Map();
  const add = (offset, id, delta) => {
    if (!points.has(offset)) points.set(offset, []);
    points.get(offset).push({ id, delta });
  };
  for (const { offset, length, id } of parseHtml(content.html).marks) {
    if (offset >= content.text.length || id.includes('\\') || !isTag(id)) continue;
    add(offset, id, 1);
    add(Math.min(offset + length, content.text.length), id, -1);
  }
  const counts = new Map();
  const result = [];
  let start = 0;
  for (const [end, changes] of [...points].sort(([a], [b]) => a - b)) {
    if (end > start && counts.size) {
      const active = [...counts.keys()];
      const quote = active.find((tag) => tag === '>^' || tag === '>');
      const code = active.includes('```') ? '```' : active.find((tag) => tag === '`');
      const ordered = code ? [quote, code].filter(Boolean) : active.sort((a, b) => Number(b === quote) - Number(a === quote));
      result.push({ offset: start, length: end - start, id: ordered.join('\\') });
    }
    for (const { id, delta } of changes) {
      const count = (counts.get(id) || 0) + delta;
      if (count) counts.set(id, count); else counts.delete(id);
    }
    start = end;
  }
  return result;
}

function fromClipboard(value) {
  const text = value.text.replace(/\r\n?/g, '\n');
  if (!text.trim() || text.length > MAX_LENGTH) throw new Error(t('replyLength'));
  if (!value.tags.length) return plainContent(text);
  const tags = checkedTags(value.tags, text, true);
  const escaped = (text) => esc(text).replace(/\n/g, '<br>');
  let html = '';
  let end = 0;
  for (const { offset, length, id } of tags) {
    html += escaped(text.slice(end, offset));
    end = offset + length;
    const part = text.slice(offset, end);
    let rendered = escaped(part);
    for (const tag of id.split('\\')) {
      const style = inlineTags.get(tag) || (tag.startsWith('```') && inlineTags.get('```'));
      const emoji = tag.match(emojiTag);
      if (style) rendered = style[0] + rendered + style[1];
      else if (emoji && /^\d{10,20}$/.test(emoji[1])) rendered = `<img alt="${esc(part)}" data-document-id="${emoji[1]}">`;
      else if (safeUrl(tag)) rendered = `<a href="${esc(tag)}">${rendered}</a>`;
    }
    html += rendered;
  }
  html += escaped(text.slice(end));
  const portable = parseHtml(html);
  return { text, html: portable.text === text ? portable.html : '', desktopTags: tags };
}

const webview = window.chrome?.webview;
const waiting = new Map();
let sequence = 0;
let busy = false;
let state;
let working;
let editingId = '';
let currentVariant = 0;
let initialDraft = '';
let editorOpen = false;
let editorTransition = Promise.resolve();
let locale = 'ru';
let messages = {};
let tipIndex = 0;
let statusTimer;
const locales = ['ru', 'en', 'zh', 'fa'];
const tipKeys = ['tipCommand', 'tipCapture', 'tipSearch', 'tipSuggest'];

function t(key, values = {}) {
  const line = messages[key]?.[locales.indexOf(locale)] ?? messages[key]?.[0] ?? key;
  return line.replace(/\{(\w+)\}/g, (token, name) => String(values[name] ?? token));
}

const field = (selector) => document.querySelector(selector);
const ui = Object.fromEntries(Object.entries({
  form: '.editing', formTitle: '.form-title', command: '.command', folder: '.folder',
  description: '.description', variants: '.variants', variantLabel: '.variant-label',
  addVariant: '.add-variant', removeVariant: '.remove-variant', richState: '.rich-state',
  capture: '.capture', copyRich: '.copy-rich', message: '.message', save: '.save', reset: '.reset',
  search: '.search', filter: '.filter', addFolder: '.add-folder', deleteFolder: '.delete-folder',
  count: '.count', list: '.list', export: '.export', import: '.import', status: '.status', hide: '.hide',
  editor: '.editor', addCommand: '.add-command', cancelEdit: '.cancel-edit', settings: '.settings-dialog',
  language: '.language', folderManager: '.folder-manager', folderStatus: '.folder-status', tip: '.tip',
  help: '.help-dialog', helpContent: '.help-content',
}).map(([key, selector]) => [key, field(selector)]));

function renderLocale() {
  document.documentElement.lang = locale === 'zh' ? 'zh-Hans' : locale;
  document.documentElement.dir = locale === 'fa' ? 'rtl' : 'ltr';
  ui.language.value = locale;
  for (const el of document.querySelectorAll('[data-text]')) el.textContent = t(el.dataset.text);
  for (const el of document.querySelectorAll('[data-label]')) {
    el.title = t(el.dataset.label);
    el.setAttribute('aria-label', el.title);
  }
  for (const el of document.querySelectorAll('[data-placeholder]')) el.placeholder = t(el.dataset.placeholder);
  updateMaximized(document.body.dataset.maximized === 'true');
  ui.tip.textContent = t(tipKeys[tipIndex]);
  ui.status.hidden = true;
  ui.tip.hidden = false;
  ui.folderStatus.hidden = true;
}

function canDiscardDraft() {
  if (!editorOpen) return true;
  syncVariant();
  return JSON.stringify(working) === initialDraft || confirm(t('discard'));
}

function showEditor(open) {
  const transition = editorTransition.then(() => setEditorOpen(open));
  editorTransition = transition.catch(() => {});
  return transition;
}

async function setEditorOpen(open) {
  if (editorOpen === open) return;
  const { style, dataset } = document.body;
  if (open) {
    const paneWidth = 450;
    const room = window.screen.availWidth || window.innerWidth;
    const libraryWidth = Math.min(window.innerWidth, 444, Math.max(274, room - paneWidth));
    style.setProperty('--library-width', libraryWidth + 'px');
    dataset.editorResizing = '';
    dataset.editor = 'open';
  }
  ui.editor.inert = true;
  try {
    if (!open) {
      const motion = await animateEditor(false);
      const libraryWidth = ui.list.closest('.library').offsetWidth;
      if (libraryWidth > 0) {
        style.setProperty('--library-width', libraryWidth + 'px');
        dataset.editorResizing = '';
      }
      ui.editor.hidden = true;
      motion?.cancel();
    }
    const width = await request('editor', open);
    editorOpen = open;
    await waitForEditorWidth(width);
    if (open) {
      delete dataset.editorResizing;
      ui.editor.hidden = false;
      const motion = await animateEditor(true);
      motion?.cancel();
    }
  } finally {
    delete dataset.editorResizing;
    ui.editor.hidden = !editorOpen;
    ui.editor.inert = !editorOpen;
    dataset.editor = editorOpen ? 'open' : 'closed';
    ui.addCommand.setAttribute('aria-expanded', String(editorOpen));
  }
}

function waitForEditorWidth(width) {
  if (!Number.isFinite(width) || Math.abs(window.innerWidth - width) <= 1) return;
  return new Promise((resolve) => {
    const finish = () => {
      clearTimeout(timeout);
      window.removeEventListener('resize', check);
      resolve();
    };
    const check = () => {
      if (Math.abs(window.innerWidth - width) <= 1) finish();
    };
    const timeout = setTimeout(finish, 120);
    window.addEventListener('resize', check);
  });
}

async function animateEditor(open) {
  if (!ui.editor.animate || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  const distance = document.dir === 'rtl' ? -32 : 32;
  const outside = { opacity: 0, transform: `translateX(${distance}px)` };
  const inside = { opacity: 1, transform: 'translateX(0)' };
  const motion = ui.editor.animate(open ? [outside, inside] : [inside, outside], {
    duration: open ? 190 : 125,
    easing: open ? 'cubic-bezier(.23, 1, .32, 1)' : 'ease-in',
    fill: 'both',
  });
  await motion.finished;
  return motion;
}

function renderHelp(markdown) {
  const sections = [];
  let list = null;
  for (const line of markdown.split(/\r?\n/)) {
    if (line.startsWith('- ')) {
      if (!list) {
        list = document.createElement('ul');
        sections.push(list);
      }
      const item = document.createElement('li');
      item.textContent = line.slice(2);
      list.append(item);
      continue;
    }
    list = null;
    if (!line.trim()) continue;
    const heading = /^#{1,3} /.test(line);
    const el = document.createElement(heading ? 'h3' : 'p');
    el.textContent = heading ? line.replace(/^#+ /, '') : line;
    sections.push(el);
  }
  ui.helpContent.replaceChildren(...sections);
}

function request(method, value) {
  if (!webview) return Promise.reject(new Error(t('openExe')));
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    waiting.set(id, { resolve, reject });
    webview.postMessage({ id, method, value: value ?? null });
  });
}

function notify(message, error = false) {
  if (ui.settings.open) {
    ui.folderStatus.textContent = message;
    ui.folderStatus.hidden = false;
    ui.folderStatus.dataset.error = String(error);
    return;
  }
  clearTimeout(statusTimer);
  ui.status.textContent = message;
  ui.status.dataset.error = String(error);
  ui.status.hidden = false;
  ui.tip.hidden = true;
  statusTimer = setTimeout(() => {
    ui.status.hidden = true;
    ui.tip.hidden = false;
  }, error ? 10000 : 4500);
}

function action(handler) {
  return async (event) => {
    event?.preventDefault();
    if (busy || !state) return;
    busy = true;
    try { await handler(event); }
    catch (error) { notify(error.message, true); }
    finally { busy = false; }
  };
}

function catalog(data) {
  return data.templates.map((template) => ({
    command: template.command, description: template.description, favorite: template.favorite,
    enabled: template.enabled,
    variants: template.variants.map(({ label, content }) => ({
      label, content: { text: content.text, tags: desktopTags(content) },
    })),
  }));
}

async function persist(next) {
  const clean = normalizeState(next);
  await request('save', { state: clean, catalog: catalog(clean) });
  state = clean;
  renderAll();
}

async function updateTemplate(id, data) {
  await persist({ ...state, templates: state.templates.map((template) => template.id === id ? { ...template, ...data } : template) });
}

async function receiveCapture(value) {
  const content = fromClipboard(value);
  await showEditor(true);
  syncVariant();
  working.variants[currentVariant].content = content;
  renderWorking();
  notify(t('captured'));
}

webview?.addEventListener('message', async ({ data }) => {
  if (data.event === 'windowState') {
    updateMaximized(data.value);
    return;
  }
  if (data.event === 'capture') {
    if (!working) return;
    try { await receiveCapture(data.value); }
    catch (error) { notify(error.message, true); }
    return;
  }
  const reply = waiting.get(data.id);
  if (!reply) return;
  waiting.delete(data.id);
  if (data.error) reply.reject(new Error(data.error)); else reply.resolve(data.value);
});

ui.form.addEventListener('submit', action(saveForm));
ui.reset.addEventListener('click', action(resetForm));
ui.addCommand.addEventListener('click', action(() => editTemplate(null)));
ui.cancelEdit.addEventListener('click', action(async () => {
  if (!canDiscardDraft()) return;
  await showEditor(false);
  resetForm();
  ui.addCommand.focus();
}));
ui.search.addEventListener('input', () => state && renderTemplates());
ui.filter.addEventListener('change', () => state && renderTemplates());
ui.addFolder.addEventListener('click', action(newFolder));
ui.deleteFolder.addEventListener('click', action(deleteFolder));
ui.folderManager.addEventListener('change', () => { ui.deleteFolder.disabled = !ui.folderManager.value; });
field('.settings').addEventListener('click', action(() => {
  ui.folderStatus.hidden = true;
  ui.settings.showModal();
}));
field('.help').addEventListener('click', action(async () => {
  renderHelp(await request('help'));
  ui.help.showModal();
}));
ui.language.addEventListener('change', action(async () => {
  const chosen = ui.language.value;
  ui.language.value = locale;
  locale = await request('language', chosen);
  if (editorOpen) syncVariant();
  renderLocale();
  renderAll();
  renderWorking();
}));
const maximize = field('.maximize');
function updateMaximized(maximized) {
  document.body.dataset.maximized = String(maximized);
  maximize.setAttribute('aria-label', maximized ? t('restore') : t('maximize'));
  maximize.title = maximize.getAttribute('aria-label');
}
const toggleMaximized = () => request('toggleMaximized');
ui.hide.addEventListener('click', action(() => request('hide')));
field('.minimize').addEventListener('click', action(() => request('minimize')));
maximize.addEventListener('click', action(toggleMaximized));
ui.addVariant.addEventListener('click', action(() => {
  syncVariant();
  if (working.variants.length >= MAX_VARIANTS) return notify(t('maxVariants'), true);
  working.variants.push({ id: newId(), label: t('variantNumber', { n: working.variants.length + 1 }), content: plainContent('') });
  currentVariant = working.variants.length - 1;
  renderWorking();
  ui.message.focus();
}));
ui.removeVariant.addEventListener('click', action(() => {
  syncVariant();
  if (working.variants.length <= 1) return;
  working.variants.splice(currentVariant, 1);
  currentVariant = Math.min(currentVariant, working.variants.length - 1);
  renderWorking();
}));
ui.variants.addEventListener('change', action(() => {
  syncVariant();
  currentVariant = Number(ui.variants.value);
  renderWorking();
}));
ui.message.addEventListener('input', () => {
  if (working && ui.message.value !== working.variants[currentVariant].content.text) {
    ui.richState.textContent = t('plainEdited');
  }
});
ui.capture.addEventListener('click', action(async () => receiveCapture(await request('clipboard'))));
ui.copyRich.addEventListener('click', action(async () => {
  syncVariant();
  const content = working.variants[currentVariant].content;
  if (!content.text.trim()) throw new Error(t('needText'));
  await request('copy', { text: content.text, tags: desktopTags(content), html: content.html });
  notify(t('copied'));
}));
ui.export.addEventListener('click', action(async () => {
  if (await request('export', { format: 'telegram-binder', ...state, exportedAt: new Date().toISOString() })) {
    notify(t('exported'));
  }
}));
ui.import.addEventListener('click', action(async () => {
  const json = await request('import');
  if (json === null) return;
  const parsed = JSON.parse(json);
  if (parsed.format !== 'telegram-binder') throw new Error(t('invalidBackup'));
  let next = parsed;
  if (parsed.version === 1 && Array.isArray(parsed.binds)) {
    next = { version: 2, folders: [], templates: parsed.binds.map((bind) => ({
      id: bind.id || newId(), command: bind.command, enabled: bind.enabled !== false,
      variants: [{ id: newId(), label: t('defaultVariant'), content: plainContent(bind.text) }],
    })) };
  }
  next = normalizeState(next);
  if (!confirm(t('confirmImport', { old: state.templates.length, next: next.templates.length }))) return;
  await persist(next);
  resetForm();
  await showEditor(false);
  notify(t('imported'));
}));

async function start() {
  try {
    const preferences = await request('preferences');
    ({ locale, messages } = preferences);
    renderLocale();
    const saved = await request('load');
    const loaded = normalizeState(saved === null ? sampleState() : JSON.parse(saved));
    await request('ready', catalog(loaded));
    state = loaded;
    renderAll();
    resetForm();
    setInterval(() => {
      tipIndex = (tipIndex + 1) % tipKeys.length;
      ui.tip.textContent = t(tipKeys[tipIndex]);
    }, 12000);
  } catch (error) {
    notify(error.message, true);
  }
}

start();

