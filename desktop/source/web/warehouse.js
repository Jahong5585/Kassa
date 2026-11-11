import {t} from './i18n.js';
import {mountManagement} from './management.js';
import {apiMoney, apiQuantity, formatMoney, money, priceLabelBarcode, quantity, RequestError, scaleQuantity, uncertain} from './model.js';
import {warehouseDemo} from './warehouse-demo.js';

const statuses = {draft: t('Черновик'), submitted: t('На проверке'), posted: t('Проведена'), cancelled: t('Отменена')};
const sections = {
  receipts: [t('Накладные'), t('Номер'), t('Дата'), t('Поставщик'), t('Статус'), t('Действия')],
  lots: [t('Складские остатки'), t('Товар и партия'), t('Срок'), t('Остаток'), t('Карантин'), t('Действия')],
  shelfTasks: [t('Задачи по снятию с полки'), t('Партия'), t('Количество'), t('Дата'), t('Действия')],
  inventoryList: [t('Инвентаризация'), t('Документ'), t('Причина'), t('Статус'), t('Действия')],
};
const date = value => value ? value.slice(0, 10).split('-').reverse().join('.') : '—';
const labelId = id => id ? id.slice(0, 8) + '…' + id.slice(-4) : '—';
const qty = (value, unit) => String(value).replace('.', ',') + (unit === 'kg' ? t(' кг') : unit === 'piece' ? t(' шт.') : '');
const statusText = status => statuses[status] ?? status;
const inventoryStatus = status => ({draft: t('Черновик'), counted: t('Пересчитан'), posted: t('Проведён')})[status] ?? status;
const itemKey = line => line.productId + ':' + line.lotId;
const errors = {
  weighed_price_label_unavailable: t('Этикетка доступна для годной весовой партии с ценой и достаточным остатком.'),
  weighed_price_label_id_reused: t('Этот код уже зарегистрирован для другой упаковки.'),
  lot_markdown_unavailable: t('Уценка доступна для годной партии в наличии. Цена должна быть ниже обычной.'),
  lot_retail_version_conflict: t('Настройки партии уже изменились. Откройте её заново.'),
  product_not_found: t('Товар по этому штрихкоду не найден. Можно предложить новую карточку.'),
  duplicate_invoice: t('Накладная с таким поставщиком, номером и датой уже есть.'),
  receipt_version_conflict: t('Накладная изменилась. Откройте актуальную версию из списка.'),
  receipt_edit_denied: t('Черновик может менять его автор или администратор.'),
  receipt_expiry_invalid: t('У одной из партий просрочен или отсутствует обязательный срок годности.'),
  receipt_order_conflict: t('Сверьте поставщика, товар и количество с остатком заказа.'),
  receipt_costs_required: t('Сначала сохраните закупочную стоимость по всем партиям.'),
  receipt_costs_stale: t('Перепроверьте закупочную стоимость после изменения документа.'),
  invoice_file_required: t('Приложите проверенную накладную.'),
  invoice_files_not_verified: t('Дождитесь проверки всех вложений или замените отклонённый файл.'),
  invoice_file_too_large: t('Выберите файл размером от 1 байта до 2 МиБ.'),
  invoice_file_duplicate: t('Этот файл уже приложен. Обновите список вложений.'),
  invalid_invoice_media_type: t('Поддерживаются PDF, JPEG и PNG.'),
  invoice_file_format_mismatch: t('Содержимое файла не совпадает с указанным форматом.'),
  stock_quantity_conflict: t('Количество превышает текущий остаток в выбранном месте хранения.'),
  inventory_version_conflict: t('Инвентаризация изменилась. Откройте актуальный документ.'),
  inventory_lot_changed: t('Остаток партии изменился. Проверьте актуальный документ и повторите пересчёт.'),
  product_version_conflict: t('Карточка изменилась. Откройте её заново.'),
  product_price_version_conflict: t('Цена изменилась. Откройте актуальную версию.'),
  purchase_order_revision_conflict: t('Версия заказа изменилась. Откройте актуальный заказ.'),
  purchase_replenishment_changed: t('Условия закупки изменились. Выполните пересчёт заказа.'),
  supplier_price_changed: t('Закупочная цена изменилась. Проверьте предложение поставщика.'),
  identity_mismatch: t('Для обновления сеанса введите данные того же магазина и пользователя.'),
  local_storage_error: t('Не удалось сохранить журнал операции на компьютере.'),
};

export function mountWarehouse({bridge, $, element, notice, emptyRow, protectedMode=()=>false, onSession=()=>{}}) {
  const demoJournal = 'kasa-store-pending-v1';
  const products = new Map();
  let role = 'receiver';
  let panel = 'receipts';
  let connection = null;
  let busy = false;
  let pending = null;
  let offset = 0;
  let count = 0;
  let receipt = null;
  let lines = [];
  let files = [];
  let order = null;
  let costs = null;
  let dirty = false;
  let costDirty = false;
  let inventoryDirty = false;
  let inventory = null;
  let inventoryInputs = [];
  let dialogSubmit = null;
  let costInputs = [];
  const admin = () => role === 'admin';
  const step = () => panel === 'lots' ? 50 : 25;
  const editable = () => receipt && (!receipt.receiptId || receipt.status === 'draft');
  const product = row => products.get(row.productId);
  const name = row => product(row)?.name ?? row.name ?? t('Товар ') + labelId(row.productId);
  const say = message => notice($('w-message'), message);
  const protectedError = () => new Error(t('Защита данных магазина включена. Снимите галочку в настройках, чтобы разрешить изменения.'));
  const error = (value, target = $('w-alert')) => {
    const base = errors[value.code] ?? (value.status === 401 ? t('Подключите сеанс с действующим токеном.') :
      value.status === 403 ? t('Нет доступа для этой роли или магазина.') : value.message && !value.code ? value.message :
      value.status ? t('Стенд отклонил операцию.') : t('Связь со стендом прервана или ответ не удалось проверить.'));
    notice(target, [base, value.code && !errors[value.code] ? value.code : '', value.row ? t('Строка ')+value.row : '', value.requestId].filter(Boolean).join(' · '));
  };

  const request = async (action, value, body) => connection
    ? bridge('store.' + action, value, body)
    : warehouseDemo(localStorage, role, action, value, body);
  const button = (label, handler, style = 'outline') => {
    const node = element('button', label, 'button ' + style);
    node.type = 'button';
    node.addEventListener('click', handler);
    return node;
  };
  const selectOption = (select, value, label) => {
    const option = element('option', label);
    option.value = value;
    select.append(option);
  };
  const cell = content => {
    const td = element('td');
    if (content instanceof HTMLElement) td.append(content);
    else td.textContent = content;
    return td;
  };
  const row = (...cells) => {
    const tr = element('tr');
    tr.append(...cells.map(cell));
    return tr;
  };

  function renderControls() {
    $('w-mode').textContent = connection ? t('Учебный стенд · ') + connection.server + ' · ' + labelId(connection.storeId) :
      t('Учебный режим · магазин 01');
    $('w-connect').disabled = busy || !bridge;
    $('w-connect').textContent = connection ? t('Обновить сеанс') : t('Подключить стенд');
    $('w-disconnect').hidden = !connection;
    $('w-disconnect').disabled = busy || Boolean(pending);
    for (const control of document.querySelectorAll('[data-w-role]')) {
      control.setAttribute('aria-pressed', String(role === control.dataset.wRole));
      control.disabled = busy || Boolean(pending);
    }
    for (const control of document.querySelectorAll('[data-w-panel]')) {
      control.hidden = !admin() && control.dataset.wPanel !== 'receipts';
      control.disabled = busy;
      control.classList.toggle('selected', panel === control.dataset.wPanel);
      control.setAttribute('aria-pressed', String(panel === control.dataset.wPanel));
    }
    const locked = busy || Boolean(pending);
    const writeLocked = locked || protectedMode();
    $('w-pending').hidden = !pending;
    $('w-retry').disabled = busy;
    $('w-dialog-ok').disabled = writeLocked;
    $('w-list').hidden = panel === 'management' || Boolean(receipt || inventory);
    $('w-management').hidden = panel !== 'management';
    management.lock(writeLocked);
    $('w-receipt').hidden = !receipt;
    $('w-inventory').hidden = !inventory;
    $('w-filter-wrap').hidden = panel !== 'receipts';
    $('w-new').hidden = panel !== 'receipts';
    $('w-new').disabled = writeLocked;
    $('w-prev').disabled = busy || offset === 0;
    $('w-next').disabled = busy || count < step() || offset >= 10000;
    $('w-add-row').hidden = !editable() || protectedMode();
    $('w-save').hidden = !editable();
    $('w-save').disabled = writeLocked || !lines.length;
    const status = receipt?.status;
    const stored = Boolean(receipt?.receiptId);
    const clean = files.length > 0 && files.every(file => file.scanStatus === 'clean');
    $('w-headers').querySelectorAll('input,select').forEach(control => { control.disabled = stored || writeLocked; });
    $('w-files-panel').hidden = !stored;
    $('w-upload').hidden = !editable();
    $('w-upload').textContent = connection ? t('Приложить файл') : t('Приложить учебный образец');
    $('w-upload').disabled = writeLocked || files.length >= 3;
    $('w-submit').hidden = status !== 'draft' || !stored;
    $('w-submit').disabled = writeLocked || !clean || dirty;
    $('w-return').hidden = !admin() || status !== 'submitted';
    $('w-return').disabled = writeLocked;
    $('w-post').hidden = !admin() || status !== 'submitted';
    $('w-post').disabled = writeLocked || costDirty || !clean || costs?.status !== 'reviewed' || costs?.receiptVersion !== receipt?.version;
    $('w-cancel').hidden = !stored || (status !== 'draft' && !(admin() && status === 'submitted'));
    $('w-cancel').disabled = writeLocked;
    $('w-cost-panel').hidden = !admin() || !['submitted', 'posted'].includes(status);
    $('w-save-cost').hidden = status !== 'submitted';
    $('w-save-cost').disabled = writeLocked;
    $('w-inventory-save').hidden = !inventory || inventory.status === 'posted';
    $('w-inventory-post').hidden = inventory?.status !== 'counted';
    $('w-inventory-save').disabled = writeLocked;
    $('w-inventory-post').disabled = writeLocked || inventoryDirty || !inventory?.lines?.length ||
      !inventory.lines.every(line => line.countedStock != null && line.countedQuarantine != null);
    $('w-propose').disabled = writeLocked;
    $('w-add-row').classList.toggle('store-protection-note', protectedMode());
  }

  async function run(work, target = $('w-alert')) {
    if (busy) return;
    busy = true;
    notice(target);
    renderControls();
    try { return await work(); }
    catch (err) { error(err, target); }
    finally { busy = false; renderControls(); }
  }

  async function checkpoint(next) {
    try {
      const text = JSON.stringify(next);
      if (connection) await bridge('store.save', text);
      else localStorage.setItem(demoJournal, text);
      pending = next;
    } catch { throw new RequestError(0, 'local_storage_error'); }
  }

  async function deliver(operation) {
    let result;
    try {
      result = await request(operation.action, operation.value, operation.body);
    } catch (err) {
      if (!uncertain(err) && ![401, 403, 429].includes(err.status)) await checkpoint(null);
      throw err;
    }
    if (!result || (operation.resultId && result[operation.idField] !== operation.resultId))
      throw new RequestError(0, 'invalid_response');
    if (operation.action === 'issuePriceLabel') {
      const [productId, lotId] = operation.value.split('|');
      const total = apiMoney(result.printedTotal);
      if (result.barcode !== priceLabelBarcode(operation.resultId) || result.productId !== productId ||
          result.lotId !== lotId || result.weight !== operation.body.weight || !total)
        throw new RequestError(0, 'invalid_response');
      say(t('Код весовой этикетки') + ' ' + result.barcode + ' · ' +
        qty(result.weight, 'kg') + ' · ' + t('На этикетке') + ' ' + formatMoney(total) + t(' сум'));
    }
    await checkpoint(null);
    return result;
  }

  async function write(action, value, body, idField, resultId) {
    if (protectedMode()) throw protectedError();
    if (pending) throw new Error(t('Сначала завершите предыдущую операцию.'));
    const entry = {action, value, body, idField, resultId, role};
    await checkpoint(entry);
    return deliver(entry);
  }

  function resetView(next = 'receipts') {
    panel = next;
    offset = count = 0;
    receipt = inventory = null;
  }

  async function list(requested = 0) {
    if (panel === 'management') return management.refresh();
    const [title, ...headers] = sections[panel];
    $('w-list-title').textContent = title;
    $('w-list-head').replaceChildren(...headers.map(text => element('th', text)));
    $('w-list-body').replaceChildren(emptyRow(headers.length, t('Загрузка…')));
    let rows;
    try {
      const data = await request(panel, String(requested), panel === 'receipts' ? $('w-filter').value : null);
      if (!Array.isArray(data)) throw new RequestError(0, 'invalid_response');
      offset = requested;
      count = data.length;
      rows = data.map(value => {
        if (panel === 'receipts') return row(value.invoiceNumber, date(value.invoiceDate), value.supplierRef,
          statusText(value.status), button(t('Открыть накладную'), () => run(() => openReceipt(value.receiptId))));
        if (panel === 'lots') {
          products.set(value.productId, value);
          const actions = element('div', null, 'actions');
          actions.append(button(t('Движение'), () => stockDialog(value)), button(t('Пересчитать'), () => inventoryCreateDialog(value)));
          if (connection) {
            actions.append(button(t('Уценка и код партии'), () => lotRetailDialog(value)));
            if (value.unit === 'kg')
              actions.append(button(t('Весовая этикетка'), () => priceLabelDialog(value)));
          }
          return row(value.name + ' · ' + labelId(value.lotId), date(value.expiryDate), qty(value.stockQuantity, value.unit),
            qty(value.quarantineQuantity, value.unit), actions);
        }
        if (panel === 'shelfTasks') return row(labelId(value.lotId), qty(value.quantity, product(value)?.unit),
          date(value.createdAt), button(t('Закрыть задачу'), () => shelfDialog(value)));
        return row(labelId(value.inventoryId), value.reason, inventoryStatus(value.status),
          button(t('Открыть пересчёт'), () => run(() => openInventory(value.inventoryId))));
      });
    } catch (err) {
      count = 0;
      $('w-list-body').replaceChildren(emptyRow(headers.length, t('Не удалось загрузить список.')));
      throw err;
    }
    $('w-list-body').replaceChildren(...(rows.length ? rows : [emptyRow(headers.length, t('Здесь пока нет записей.'))]));
  }

  async function newReceipt() {
    if (pending) return;
    const suppliers = await request('suppliers', '0');
    const orders = await request('orders', '0');
    receipt = {status: 'draft'};
    lines = [];
    files = [];
    costs = order = null;
    dirty = costDirty = false;
    $('w-suppliers').replaceChildren(...suppliers.map(supplier => {
      const option = element('option', supplier.name);
      option.value = supplier.code;
      return option;
    }));
    $('w-supplier').value = suppliers.length === 1 ? suppliers[0].code : '';
    $('w-number').value = '';
    $('w-date').value = new Intl.DateTimeFormat('en-CA', {timeZone:'Asia/Tashkent', dateStyle:'short'}).format(new Date());
    $('w-order').replaceChildren();
    selectOption($('w-order'), '', t('Без заказа'));
    for (const entry of orders) selectOption($('w-order'), entry.orderId, entry.supplierRef + ' · ' + labelId(entry.orderId));
    renderReceipt();
    $('w-number').focus();
  }

  async function openReceipt(id) {
    const document = await request('receipt', id);
    if (document?.receiptId !== id || !Array.isArray(document.items)) throw new RequestError(0, 'invalid_response');
    if (!connection) {
      for (const product of await request('productInfo')) products.set(product.productId, product);
    }
    const attachments = await request('files', id);
    if (!Array.isArray(attachments)) throw new RequestError(0, 'invalid_response');
    let checkedCosts = null;
    if (admin() && ['submitted','posted'].includes(document.status)) {
      try { checkedCosts = await request('costs', id); }
      catch (err) { if (err.status !== 404) throw err; }
    }
    order = document.purchaseOrderId ? await request('order', document.purchaseOrderId) : null;
    receipt = document;
    lines = document.items.map(line => ({...line}));
    files = attachments;
    costs = checkedCosts;
    dirty = costDirty = false;
    $('w-supplier').value = document.supplierRef;
    $('w-number').value = document.invoiceNumber;
    $('w-date').value = document.invoiceDate;
    $('w-order').replaceChildren();
    selectOption($('w-order'), document.purchaseOrderId ?? '', document.purchaseOrderId ? labelId(document.purchaseOrderId) : t('Без заказа'));
    renderReceipt();
  }

  function renderOrder() {
    if (!order) return notice($('w-order-summary'));
    const summaries = order.lines.map(line => {
      const current = lines.filter(item => item.productId === line.productId)
        .reduce((sum, item) => sum + (quantity(item.quantity) ?? 0), 0) / 1000;
      return name(line) + ' · ' + (receipt.status === 'posted' ? t('по заказу ') + line.ordered + t(', принято ') + line.received :
        t('доступно по заказу ') + line.remaining + t(', в накладной ') + current);
    });
    notice($('w-order-summary'), summaries.join(' / '));
  }

  function itemInput(value, type, label, change, disabled) {
    const field = element('input');
    field.value = value ?? '';
    field.type = type;
    if (type === 'text') { field.inputMode = 'decimal'; field.maxLength = 18; }
    field.setAttribute('aria-label', label);
    field.disabled = disabled;
    field.addEventListener('input', () => change(field.value));
    return field;
  }

  function renderReceipt() {
    const stored = Boolean(receipt.receiptId);
    $('w-receipt-title').textContent = stored ? t('Накладная ') + receipt.invoiceNumber : t('Новая накладная');
    const version = stored ? statusText(receipt.status) + t(' · версия ') + receipt.version + ' · ' + receipt.receiptId + '. ' : '';
    const detail = receipt.status === 'posted' ? t('Товар на остатке.') : receipt.status === 'cancelled' ?
      t('Отменена. ') + (receipt.cancellation?.reason ?? '') : t('Товар появится на складе после проведения.');
    $('w-receipt-note').textContent = version + detail + (receipt.reviewNote ? t(' Правка: ') + receipt.reviewNote : '');
    renderOrder();
    const allow = editable();
    const children = lines.map((line, index) => {
      const markDirty = () => { dirty = true; renderOrder(); renderControls(); };
      const id = labelId(line.lotId);
      const dateInput = itemInput(line.expiryDate, 'date', t('Срок партии ') + (index + 1), value => {
        line.expiryDate = value || null; markDirty();
      }, !allow);
      const countInput = itemInput(line.quantity, 'text', t('Факт партии ') + (index + 1), value => {
        line.quantity = value; markDirty();
      }, !allow);
      const remove = allow ? button(t('Убрать'), () => {
        if (busy || pending) return;
        lines.splice(index, 1); dirty = true; renderReceipt();
      }) : '—';
      const item = product(line);
      return row(name(line) + (item?.unit === 'kg' ? t(' · кг') : item?.unit === 'piece' ? t(' · шт.') : ''),
        id, dateInput, countInput, remove);
    });
    $('w-items').replaceChildren(...(children.length ? children : [emptyRow(5, t('Добавьте товар по штрихкоду.'))]));
    renderFiles();
    renderCosts();
    renderControls();
  }

  function renderFiles() {
    const fileRows = files.map(file => {
      const actions = element('div', null, 'actions');
      if (file.scanStatus === 'pending') actions.append(button(t('Проверить файл'), () => fileAction('scan', file.fileId)));
      if (editable()) actions.append(button(t('Удалить файл'), () => fileAction('delete', file.fileId), 'danger'));
      if (connection && admin() && file.scanStatus === 'clean')
        actions.append(button(t('Сохранить оригинал'), () => fileAction('download', file.fileId)));
      const wrapper = element('div', null, 'result');
      const label = {clean: t('Проверен'), pending: t('Ожидает проверки'), rejected: t('Отклонён')}[file.scanStatus] ?? file.scanStatus;
      wrapper.append(element('span', file.fileName + ' · ' + label), actions);
      return wrapper;
    });
    $('w-files').replaceChildren(...(fileRows.length ? fileRows : [element('p', t('Вложения пока отсутствуют.'))]));
  }

  function renderCosts() {
    const keys = ['unitPrice','discount','includedTax','allocatedCharges'];
    $('w-cost-summary').textContent = costs ? (costs.status === 'stale' ? t('Требуется повторная проверка. ') : '') +
      t('Итого ') + formatMoney(Math.round(costs.total * 100)) + t(' сум.') : t('Заполните цену за единицу по каждой партии.');
    costInputs = lines.map((line, index) => {
      const previous = costs?.lines?.find(item => itemKey(item) === itemKey(line));
      const inputs = keys.map((key, n) => itemInput(previous?.[key] ?? (n ? 0 : ''), 'text',
        [t('Цена партии '),t('Скидка партии '),t('Налог партии '),t('Расходы партии ')][n] + (index + 1), () => { costDirty = true; renderControls(); }, receipt.status !== 'submitted'));
      return {line, inputs, keys};
    });
    $('w-costs').replaceChildren(...costInputs.map(({line, inputs}) => row(name(line) + ' · ' + labelId(line.lotId), ...inputs)));
  }

  async function saveReceipt() {
    const items = lines.map(line => {
      const units = quantity(line.quantity);
      if (!units || (product(line)?.unit === 'piece' && units % 1000)) throw new Error(t('Введите допустимое количество для каждой партии.'));
      return {productId: line.productId, lotId: line.lotId, expiryDate: line.expiryDate || null, quantity: units / 1000};
    });
    const id = receipt.receiptId;
    if (id) await write('edit', id, {expectedVersion: receipt.version, items}, 'receiptId', id);
    else {
      const supplierRef = $('w-supplier').value.trim();
      const invoiceNumber = $('w-number').value.trim();
      const invoiceDate = $('w-date').value;
      if (!supplierRef || !invoiceNumber || !invoiceDate) throw new Error(t('Заполните поставщика, номер и дату накладной.'));
      const receiptId = crypto.randomUUID();
      await write('create', null, {receiptId, supplierRef, invoiceNumber, invoiceDate, items, purchaseOrderId: order?.orderId ?? null},
        'receiptId', receiptId);
      await openReceipt(receiptId);
      say(t('Черновик сохранён. Приложите накладную.'));
      return;
    }
    await openReceipt(id);
    say(t('Изменения черновика сохранены.'));
  }

  async function transition(action, extra = {}) {
    const id = receipt.receiptId;
    await write(action, id, {expectedVersion: receipt.version, ...extra}, 'receiptId', id);
    await openReceipt(id);
    say({submit: t('Накладная передана на проверку.'), post: t('Накладная проведена. Остатки обновлены.'),
      return: t('Накладная возвращена на правку.'), cancel: t('Накладная отменена.')}[action]);
  }

  function fileAction(action, id) {
    if (pending) return;
    if (protectedMode() && action !== 'download') { error(protectedError()); return; }
    const perform = async () => {
      try {
        const result = await request(action, receipt.receiptId, id);
        if (action === 'download') { if (result) say(t('Оригинал сохранён.')); return; }
        if (result == null) return;
      } catch (err) {
        if (action !== 'upload' || !uncertain(err)) throw err;
        const current = await request('files', receipt.receiptId);
        if (!current.some(file => file.fileId === id)) throw err;
      }
      files = await request('files', receipt.receiptId);
      renderFiles();
    };
    if (action === 'delete') ask(t('Удалить вложение'), t('Файл будет удалён из черновика.'), [], perform, t('Удалить'));
    else run(perform);
  }

  async function saveCosts() {
    const items = costInputs.map(({line, inputs, keys}) => {
      const amounts = inputs.map(input => money(input.value));
      if (amounts.some(amount => amount == null)) throw new Error(t('Проверьте суммы. Допустимо до двух знаков после запятой.'));
      return {productId: line.productId, lotId: line.lotId,
        ...Object.fromEntries(keys.map((key, i) => [key, amounts[i] / 100]))};
    });
    const id = receipt.receiptId;
    await write('saveCosts', id, {expectedVersion: receipt.version, items}, 'receiptId', id);
    await openReceipt(id);
    say(t('Закупочная стоимость сохранена.'));
  }

  function ask(title, detail, fields, execute, confirm = t('Подтвердить')) {
    if (busy || pending) return;
    if (protectedMode()) { error(protectedError()); return; }
    $('w-dialog-title').textContent = title;
    $('w-dialog-detail').textContent = detail;
    $('w-dialog-ok').textContent = confirm;
    const values = new Map();
    $('w-dialog-fields').replaceChildren(...fields.map(field => {
      const wrapper = element('label', null, 'field');
      const control = element(field.options ? 'select' : field.multiline ? 'textarea' : 'input');
      if (field.options) for (const [value, label] of field.options) selectOption(control, value, label);
      else {
        control.maxLength = field.maxLength ?? 500;
        if (field.type) control.type = field.type;
        control.inputMode = field.inputMode ?? 'text';
        if (field.multiline) control.rows = 7;
        control.required = !field.optional;
      }
      control.value = field.value ?? control.value;
      control.setAttribute('aria-label', field.label);
      wrapper.append(element('span', field.label), control);
      values.set(field.key, control);
      return wrapper;
    }));
    dialogSubmit = () => execute(Object.fromEntries([...values].map(([key, control]) => [key, control.value.trim()])));
    notice($('w-dialog-error'));
    $('w-dialog').showModal();
  }

  const reasonField = {key: 'reason', label: t('Причина')};
  function withReason(action, title) {
    ask(title, t('Накладная ') + receipt.invoiceNumber, [reasonField], ({reason}) => {
      if (!reason) throw new Error(t('Укажите причину.'));
      return transition(action, {reason});
    });
  }

  function proposalDialog() {
    const fields = [{key:'name', label:t('Название товара'), maxLength:160},
      {key:'barcode', label:t('Штрихкод'), maxLength:48, value:$('w-barcode').value},
      {key:'unit', label:t('Единица'), options:[['piece',t('Штуки')],['kg',t('Килограммы')]]},
      {key:'expiry', label:t('Контроль срока'), options:[['no',t('Нет')],['yes',t('Обязателен')]]}];
    ask(t('Предложить карточку'), t('Карточка поступит администратору на рассмотрение.'), fields, async values => {
      if (!values.name || !/^\d{1,48}$/.test(values.barcode)) throw new Error(t('Введите название и корректный штрихкод.'));
      const proposalId = crypto.randomUUID();
      await write('propose', null, {proposalId, product: {name: values.name, unit: values.unit, sku: null,
        category: null, requiresExpiry: values.expiry === 'yes', barcodes: [{code: values.barcode, quantityFactor: 1}]}},
        'proposalId', proposalId);
      say(t('Предложение зарегистрировано.'));
    }, t('Передать предложение'));
  }

  function shelfDialog(task) {
    ask(t('Закрыть задачу по снятию'), t('Партия ') + labelId(task.lotId) + t('. Укажите результат проверки партии.'), [reasonField], async ({reason}) => {
      if (!reason) throw new Error(t('Укажите результат проверки.'));
      await write('resolveShelf', task.taskId, {resolution: reason}, 'taskId', task.taskId);
      await list(offset);
      say(t('Задача закрыта.'));
    });
  }

  function stockDialog(lot) {
    const fields = [
      {key:'kind', label:t('Операция'), options:[['quarantine',t('Переместить в карантин')],['writeOffStock',t('Списать с остатка')],
        ['writeOffQuarantine',t('Списать из карантина')]]},
      {key:'quantity', label:t('Количество'), inputMode:'decimal', maxLength:18}, reasonField,
    ];
    ask(t('Движение партии'), lot.name + ' · ' + labelId(lot.lotId), fields, async values => {
      const units = quantity(values.quantity);
      const available = values.kind === 'writeOffQuarantine' ? lot.quarantineQuantity : lot.stockQuantity;
      if (!values.reason || !units || (lot.unit === 'piece' && units % 1000) || units > apiQuantity(available))
        throw new Error(t('Укажите причину и допустимое количество в пределах остатка.'));
      const operationId = crypto.randomUUID();
      await write('stockOperation', null, {operationId, productId:lot.productId, lotId:lot.lotId, kind:values.kind,
        quantity:units / 1000, reason:values.reason}, 'operationId', operationId);
      await list(offset);
      say(t('Складское движение проведено.'));
    }, t('Провести движение'));
  }

  async function lotRetailDialog(lot) {
    const key = lot.productId + '|' + lot.lotId;
    let current;
    await run(async () => {
      const result = await request('lotRetail', key);
      if (result?.lotId !== lot.lotId || result.productId !== lot.productId || !Number.isInteger(result.version))
        throw new RequestError(0, 'invalid_response');
      current = result;
    });
    if (!current) return;
    const detail = [lot.name + ' · ' + labelId(lot.lotId),
      current.baseUnitPrice == null ? t('Обычная цена не задана') :
        t('Обычная цена ') + formatMoney(money(current.baseUnitPrice)) + t(' сум'),
      current.labelCode ? t('Код партии ') + current.labelCode : t('Код партии будет назначен при сохранении')].join(' · ');
    ask(t('Уценка и код партии'), detail, [
      {key:'price', label:t('Цена партии, сум. Пустое поле снимает уценку'), value:current.unitPrice ?? '',
        inputMode:'decimal', optional:true, maxLength:18}, reasonField,
    ], async ({price, reason}) => {
      const cents = price ? money(price) : null;
      if (!reason || (price && !cents)) throw new Error(t('Укажите причину и допустимую цену партии.'));
      const saved = await write('saveLotRetail', key,
        {expectedVersion:current.version, unitPrice:cents == null ? null : cents / 100, reason}, 'lotId', lot.lotId);
      if (!saved?.labelCode || saved.version !== current.version + 1) throw new RequestError(0, 'invalid_response');
      say(t('Код партии ') + saved.labelCode);
    }, t('Сохранить настройки партии'));
  }

  function priceLabelDialog(lot) {
    const key = lot.productId + '|' + lot.lotId;
    ask(t('Весовая этикетка'), lot.name + ' · ' + labelId(lot.lotId) + ' · ' +
      t('Взвесьте упаковку и введите вес нетто. Сумму для этикетки рассчитает сервер.'), [
      {key:'weight', label:t('Вес нетто, кг'), inputMode:'decimal', maxLength:18},
    ], async ({weight}) => {
      const grams = scaleQuantity(weight);
      if (!grams) throw new Error(t('Укажите вес больше нуля с точностью до грамма.'));
      const labelId = crypto.randomUUID();
      await write('issuePriceLabel', key, {labelId, weight:grams / 1000}, 'labelId', labelId);
    }, t('Создать код этикетки'));
  }

  function inventoryCreateDialog(lot) {
    ask(t('Начать пересчёт партии'), lot.name + ' · ' + labelId(lot.lotId), [reasonField], async ({reason}) => {
      if (!reason) throw new Error(t('Укажите причину пересчёта.'));
      const inventoryId = crypto.randomUUID();
      await write('inventoryCreate', null, {inventoryId, reason, lots:[{productId:lot.productId, lotId:lot.lotId}]},
        'inventoryId', inventoryId);
      resetView('inventoryList');
      await openInventory(inventoryId);
      say(t('Пересчёт создан.'));
    });
  }

  async function openInventory(id) {
    const doc = await request('inventory', id);
    if (doc?.inventoryId !== id || !Array.isArray(doc.lines)) throw new RequestError(0, 'invalid_response');
    inventory = doc;
    inventoryDirty = false;
    $('w-inventory-note').textContent = inventoryStatus(doc.status) +
      t(' · версия ') + doc.version + ' · ' + doc.reason;
    const change = () => { inventoryDirty = true; renderControls(); };
    inventoryInputs = doc.lines.map((line, index) => {
      const stock = itemInput(line.countedStock, 'text', t('Фактический остаток ') + (index + 1), change, doc.status === 'posted');
      const quarantine = itemInput(line.countedQuarantine, 'text', t('Фактический карантин ') + (index + 1), change, doc.status === 'posted');
      return {line, stock, quarantine, node: row(name(line) + ' · ' + labelId(line.lotId),
        (line.expectedStock ?? '—') + ' / ' + (line.expectedQuarantine ?? '—'), stock, quarantine)};
    });
    $('w-inventory-lines').replaceChildren(...inventoryInputs.map(input => input.node));
    renderControls();
  }

  async function saveInventory() {
    const items = inventoryInputs.map(({line, stock, quarantine}) => {
      const stockUnits = quantity(stock.value);
      const quarantineUnits = quantity(quarantine.value);
      if (stockUnits == null || quarantineUnits == null || (line.unit === 'piece' && (stockUnits % 1000 || quarantineUnits % 1000)))
        throw new Error(t('Заполните фактические остатки. Для пустого места укажите 0.'));
      return {productId:line.productId, lotId:line.lotId, lotRevision:line.lotRevision,
        stockQuantity:stockUnits / 1000, quarantineQuantity:quarantineUnits / 1000};
    });
    const id = inventory.inventoryId;
    await write('inventoryCount', id, {expectedVersion:inventory.version, items}, 'inventoryId', id);
    await openInventory(id);
    say(t('Фактические остатки записаны.'));
  }

  function connect() {
    return run(async () => {
      const current = await bridge('store.connect', role);
      if (!current) return null;
      connection = current;
      onSession(current);
      management.reset();
      const saved = await bridge('store.load');
      pending = saved ? JSON.parse(saved) : null;
      products.clear();
      resetView();
      await list();
      return current;
    });
  }

  function switchRole(next) {
    if (next === role || pending || busy) return;
    run(async () => {
      if (connection) {
        await bridge('store.disconnect');
        connection = null;
        products.clear();
      }
      role = next;
      management.reset();
      resetView();
      say('');
      await list();
    });
  }

  async function activate(next) {
    if (!['receiver','admin'].includes(next) || pending || busy) return null;
    if (!bridge) {
      role = next;
      management.reset();
      resetView();
      await list();
      return {role:next, roles:{roles:['cashier','receiver','admin'], owner:false}};
    }
    if (connection && role !== next) {
      await bridge('store.disconnect');
      connection = null;
      products.clear();
    }
    role = next;
    management.reset();
    resetView();
    say('');
    return connect();
  }

  async function retry() {
    if (!pending) return;
    const operation = pending;
    if (!connection && operation.role && operation.role !== role) return;
    await deliver(operation);
    if (operation.action !== 'issuePriceLabel') say(t('Операция подтверждена.'));
    if (operation.action.startsWith('management.')) { resetView('management'); await management.refresh(); }
    else if (operation.idField === 'receiptId') await openReceipt(operation.resultId);
    else if (operation.idField === 'inventoryId') await openInventory(operation.resultId);
    else await list(offset);
  }

  const management = mountManagement({bridge, $, element, notice, emptyRow, request, write, run, ask, say, admin, online:()=>Boolean(connection)});

  $('w-connect').addEventListener('click', connect);
  $('w-disconnect').addEventListener('click', () => run(async () => {
    if (pending) return;
    await bridge('store.disconnect');
    connection = null;
    management.reset();
    products.clear();
    pending = JSON.parse(localStorage.getItem(demoJournal) ?? 'null');
    if (pending?.role) role = pending.role;
    resetView();
    await list();
  }));
  $('w-retry').addEventListener('click', () => run(retry));
  for (const control of document.querySelectorAll('[data-w-role]'))
    control.addEventListener('click', () => switchRole(control.dataset.wRole));
  for (const control of document.querySelectorAll('[data-w-panel]'))
    control.addEventListener('click', () => run(async () => { resetView(control.dataset.wPanel); say(''); await list(); }));
  $('w-filter').addEventListener('change', () => run(() => list()));
  $('w-refresh').addEventListener('click', () => run(() => list()));
  $('w-prev').addEventListener('click', () => run(() => list(Math.max(0, offset - step()))));
  $('w-next').addEventListener('click', () => run(() => list(offset + step())));
  $('w-new').addEventListener('click', () => run(newReceipt));
  $('w-close').addEventListener('click', () => run(async () => { receipt = null; say(''); await list(offset); }));
  $('w-order').addEventListener('change', () => run(async () => {
    const id = $('w-order').value;
    order = id ? await request('order', id) : null;
    if (order) $('w-supplier').value = order.supplierRef;
    renderOrder();
  }));
  $('w-scan').addEventListener('submit', event => {
    event.preventDefault();
    if (!editable() || pending) return;
    run(async () => {
      const barcode = $('w-barcode').value.trim();
      if (!/^\d{1,48}$/.test(barcode)) throw new Error(t('Введите цифры штрихкода.'));
      if (lines.length >= 100) throw new Error(t('В накладной может быть до 100 партий.'));
      const item = await request('barcode', barcode);
      if (!item?.productId) throw new RequestError(0, 'invalid_response');
      products.set(item.productId, item);
      lines.push({productId:item.productId, lotId:crypto.randomUUID(), expiryDate:null, quantity:item.quantityFactor ?? 1});
      $('w-barcode').value = '';
      dirty = true;
      renderReceipt();
    });
  });
  $('w-propose').addEventListener('click', proposalDialog);
  $('w-save').addEventListener('click', () => run(saveReceipt));
  $('w-upload').addEventListener('click', () => fileAction('upload', crypto.randomUUID()));
  $('w-submit').addEventListener('click', () => run(() => transition('submit')));
  $('w-post').addEventListener('click', () => ask(t('Провести накладную'), t('Партии поступят на остаток магазина.'), [],
    () => transition('post'), t('Провести')));
  $('w-return').addEventListener('click', () => withReason('return', t('Вернуть на правку')));
  $('w-cancel').addEventListener('click', () => withReason('cancel', t('Отменить накладную')));
  $('w-save-cost').addEventListener('click', () => run(saveCosts));
  $('w-inventory-close').addEventListener('click', () => run(async () => { inventory = null; say(''); await list(); }));
  $('w-inventory-save').addEventListener('click', () => run(saveInventory));
  $('w-inventory-post').addEventListener('click', () => ask(t('Провести пересчёт'), t('Подтверждённый факт станет остатком партии.'), [], async () => {
    const id = inventory.inventoryId;
    await write('inventoryPost', id, {expectedVersion:inventory.version}, 'inventoryId', id);
    await openInventory(id);
    say(t('Пересчёт проведён.'));
  }, t('Провести')));
  $('w-dialog-form').addEventListener('submit', event => {
    event.preventDefault();
    if (busy || !dialogSubmit) return;
    const submit = dialogSubmit;
    run(async () => {
      await submit();
      $('w-dialog').close();
    }, $('w-dialog-error'));
  });
  $('w-dialog').addEventListener('cancel', event => { if (busy) event.preventDefault(); });
  $('w-dialog').querySelectorAll('[data-dismiss]').forEach(control => control.addEventListener('click', event => {
    if (busy) event.stopImmediatePropagation();
  }, true));
  try {
    pending = JSON.parse(localStorage.getItem(demoJournal) ?? 'null');
    if (pending?.role) role = pending.role;
  } catch (err) { error(err); }
  renderControls();
  return {
    enter: () => run(() => receipt || inventory ? Promise.resolve() : list(offset)),
    isBusy: () => busy,
    activate,
    setProtected: () => renderControls(),
  };
}
