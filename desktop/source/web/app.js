import {t} from './i18n.js';
import {mountPilot} from './pilot.js';
import {
  DemoApi, RequestError, apiMoney, apiQuantity, basketDiscount, basketTotal, createBridge, emptyState,
  demoRegister, findOutcome, formatMoney, isPriceLabelBarcode, isSaleBarcode, lineTotal, money,
  priceLabelBarcode, quantity, scaleQuantity, receiptMark, refundCents, uncertain, validateOutcome,
} from './model.js';
import {mountWarehouse} from './warehouse.js';

const $ = id => document.getElementById(id);
const demo = new DemoApi(localStorage);
const bridge = createBridge(window.chrome?.webview);
const dateTime = new Intl.DateTimeFormat('ru-RU', {
  dateStyle: 'short', timeStyle: 'short', timeZone: 'Asia/Tashkent',
});
const demoKey = 'kasa-demo-desktop-v1';
const pageSize = 25;
const scanQueueLimit = 50;
const quickLimit = 8;
let connection = null;
let quickProducts = [];
let quickEditing = false;
let verified = true;
let state = emptyState();
let page = 'sale';
let busy = false;
let searchQueue = Promise.resolve();
let searches = 0;
let offset = 0;
let historyCount = 0;
let amountAction;
let lotProduct;
let canRepeat = false;
let toastTimer;
let historyVersion = 0;
let receiptQuery = '';
let expiryFilter = 'within7';
let expiryOffset = 0;
let expiryCount = 0;
let movementOffset = 0;
let movementCount = 0;
let movementDirection;
let refundDraft;
let shelfLot;
let cartOffset = 0;
const expiryPageSize = 50;
const shelfMarks = new Map();
const lotKey = row => row.productId + ':' + row.lotId;
const lotLabel = id => id.slice(0, 4) + '…' + id.slice(-8);
const currentRegister = () => connection?.register ?? demoRegister;
const operationCollisions = new Set(['operation_id_reused', 'cash_movement_id_reused', 'shelf_task_id_reused', 'parked_cart_id_reused']);
const protectionKey = 'kasa-store-protection-v1';
const roleLabels = {cashier:t('Кассир'), receiver:t('Приёмщик'), admin:t('Администратор')};
let storeProtected = localStorage.getItem(protectionKey) !== 'off';
let activeRole = 'cashier';
let availableRoles = ['cashier', 'receiver', 'admin'];
let ownerRole = false;

const errors = {
  refund_quantity_exceeded: t('Доступное количество изменилось. Снова откройте возврат из исходного чека.'),
  refund_amount_changed: t('Сумма изменилась. Проверьте исходный чек и создайте новый запрос на возврат.'),
  refund_approval_conflict: t('Условия возврата изменились. Проверьте исходный чек.'),
  insufficient_cash_balance: t('В кассе недостаточно наличных для этой операции.'),
  shelf_task_already_open: t('По этой партии уже есть открытая задача.'),
  shelf_task_quantity_exceeded: t('Снятое количество превышает доступный остаток партии.'),
  admin_mfa_required: t('Для подтверждения требуется сеанс администратора с MFA.'),
  product_not_found: t('Товар не найден. Проверьте код или попробуйте поиск по названию.'),
  sale_price_not_set: t('На товар не установлена розничная цена.'),
  price_changed: t('Цена изменилась. Удалите позицию и добавьте её заново.'),
  lot_price_changed: t('Цена партии изменилась. Добавьте её заново или повторно восстановите отложенный чек.'),
  invalid_weighed_price_label: t('Код весовой этикетки повреждён. Повторите сканирование.'),
  weighed_price_label_not_found: t('Весовая этикетка не найдена в этом магазине. Обратитесь к администратору.'),
  weighed_price_label_unavailable: t('Весовая этикетка уже использована или недоступна. Обратитесь к администратору.'),
  weighed_price_label_changed: t('Сумма на весовой этикетке изменилась. Удалите позицию и отсканируйте этикетку ещё раз.'),
  invalid_lot_barcode: t('Код партии повреждён. Повторите сканирование.'),
  lot_barcode_expired: t('Срок годности этой партии истёк. Продажа заблокирована.'),
  lot_barcode_unavailable: t('Эта партия недоступна для продажи.'),
  quantity_promotion_changed: t('Акция 2+1 изменилась. Добавьте товар заново или восстановите отложенный чек ещё раз.'),
  lot_not_sellable_or_insufficient_stock: t('Партия недоступна или остатка недостаточно. Найдите товар заново.'),
  stock_unavailable: t('Остатка партии недостаточно.'),
  open_shift_conflict: t('У кассы уже есть открытая смена. Если она открыта на другом компьютере, обратитесь к администратору.'),
  shift_not_found: t('Смена недоступна для этой кассы и учётной записи.'),
  active_shift_required: t('Смена закрыта или недоступна. Проверьте состояние смены.'),
  shift_has_parked_carts: t('В смене есть отложенные корзины. Завершите их перед закрытием.'),
  parked_cart_not_found: t('Отложенный чек недоступен. Обновите список.'),
  parked_cart_version_conflict: t('Отложенный чек изменился. Верните его в список и проверьте заново.'),
  local_storage_error: t('Не удалось прочитать или сохранить локальные данные. Проверьте свободное место и доступ к профилю Windows.'),
  timeout: t('Стенд не ответил вовремя.'),
  connection_failed: t('Нет связи с учебным API. Проверьте адрес, сеть и сертификат.'),
  invalid_response: t('Сервер вернул неожиданный ответ. Проверьте версию API.'),
  identity_mismatch: t('Для восстановления операции подключите ту же кассу и того же кассира.'),
  pilot_profile_invalid: t('Проверьте параметры KasaPilot.json.'),
  pilot_register_required: t('Укажите ID кассы в KasaPilot.json.'),
  pilot_store_required: t('Укажите ID магазина в KasaPilot.json.'),
  pilot_register_key_invalid: t('Проверьте ключ кассы.'),
  browser_open_failed: t('Не удалось открыть системный браузер.'),
  browser_login_timeout: t('Время входа истекло. Повторите попытку.'),
  identity_connection_failed: t('Не удалось связаться с сервером входа.'),
  identity_issuer_mismatch: t('Адрес поставщика входа не совпадает с профилем.'),
  identity_pkce_unsupported: t('Сервер входа не поддерживает PKCE S256.'),
  identity_discovery_invalid: t('Сервер входа вернул некорректную конфигурацию.'),
  identity_request_rejected: t('Сервер входа отклонил запрос.'),
  identity_login_denied: t('Вход отменён или отклонён.'),
  identity_token_invalid: t('Сервер входа не вернул подходящий токен доступа.'),
  invalid_stand_identity: t('Токен не содержит идентификатор кассира. Получите новый токен на учебном стенде.'),
  cash_tendered_below_total: t('Внесённой суммы недостаточно для оплаты.'),
};

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function notice(node, message = '') {
  node.textContent = message;
  node.hidden = !message;
}

function describe(error) {
  let message = errors[error.code];
  if (!message) {
    if (error.status === 401) message = t('Срок токена истёк или вход не подтверждён. Обновите сеанс в настройках.');
    else if (error.status === 403) message = t('Доступ запрещён. Проверьте роль кассира, назначение и ключ кассы.');
    else if (error.status === 429) message = t('Слишком много запросов. Повторите через несколько секунд.');
    else message = error.status ? t('Сервер отклонил запрос.') : t('Не удалось завершить действие.');
  }
  return [message, error.status ? error.code : '', error.requestId ? t('Запрос ') + error.requestId : ''].filter(Boolean).join(' · ');
}

function toast(message) {
  clearTimeout(toastTimer);
  notice($('toast'), message);
  toastTimer = setTimeout(() => notice($('toast')), 3300);
}

function renderRolePicker() {
  const picker = $('active-role');
  picker.replaceChildren(...availableRoles.map(role => {
    const option = element('option', roleLabels[role] ?? role);
    option.value = role;
    return option;
  }));
  if (availableRoles.includes(activeRole)) picker.value = activeRole;
  picker.disabled = busy || searches > 0 || availableRoles.length < 2;
  $('owner-badge').hidden = !ownerRole;
}

function applyRoleSession(selection, selected) {
  const roles = Array.isArray(selection?.roles)
    ? selection.roles.filter(role => Object.hasOwn(roleLabels, role)) : [];
  availableRoles = roles.length ? roles : [selected];
  ownerRole = Boolean(selection?.owner);
  activeRole = availableRoles.includes(selected) ? selected : availableRoles[0];
  renderRolePicker();
}

const apiLabels = {
  online: [t('● API на связи'), 'green'],
  silent: [t('◷ API не отвечает'), 'gold'],
  failed: [t('! Сбой API'), 'red'],
  expired: [t('◷ Сеанс истёк'), 'gold'],
  denied: [t('! Доступ к API закрыт'), 'red'],
};

function setApiState(state) {
  const badge = $('api-status');
  const [text, color] = apiLabels[state] ?? ['', ''];
  badge.hidden = !connection || !state;
  if (badge.textContent !== text) badge.textContent = text;
  badge.className = 'badge ' + color;
}

function recordApiError(error) {
  if (error.status === 401) setApiState('expired');
  else if (error.status === 403) setApiState('denied');
  else if (error.status === 408 || ['timeout', 'connection_failed', 'desktop_timeout'].includes(error.code)) setApiState('silent');
  else if (error.status >= 500 || error.code === 'invalid_response') setApiState('failed');
}

async function call(action, value, body) {
  if (!connection) return demo.call(action, value, body);
  try {
    const data = await bridge(action, value, body);
    setApiState('online');
    return data;
  } catch (error) {
    recordApiError(error);
    throw error;
  }
}

function decodeState(raw) {
  if (raw == null) return emptyState();
  const result = JSON.parse(raw);
  if (![1, 2, 3, 4, 5].includes(result?.version) || !Array.isArray(result.basket)) throw new RequestError(0, 'local_storage_error');
  return result;
}

async function save(next) {
  try {
    const items = [...next.basket, ...(next.pending?.body?.items ?? [])];
    const version = items.some(item => item.priceLabelId) ? 5 : items.some(item => item.lotPriceVersion) ? 4 :
      items.some(item => item.promotionVersion) ? 3 : next.cart || next.pending?.kind === 'parkCart' ? 2 : 1;
    next = {...next, version};
    const raw = JSON.stringify(next);
    if (connection) await bridge('save', raw);
    else localStorage.setItem(demoKey, raw);
    state = next;
  } catch {
    throw new RequestError(0, 'local_storage_error');
  }
}

function active() {
  return verified && Boolean(state.shift && !state.shift.closedAt);
}

function canEdit() {
  return active() && !state.pending && !busy;
}

function canQueueSaleInput() {
  return active() && !state.pending && !state.cart && (!busy || searches > 0);
}

function canPay() {
  const total = basketTotal(state.basket);
  return canEdit() && searches === 0 && total > 0 && money(total / 100) != null;
}

function hideResults() {
  $('results').hidden = true;
  $('result-items').replaceChildren();
}

function scanFocus() {
  if (page === 'sale' && canEdit() && !state.cart && !document.querySelector('dialog[open]')) $('search').focus({preventScroll: true});
}

function showPage(next) {
  if (busy || searches || warehouse.isBusy()) return;
  page = next;
  notice($('alert'));
  for (const tab of document.querySelectorAll('[data-page]')) {
    const selected = tab.dataset.page === page;
    $(tab.dataset.page).hidden = !selected;
    tab.classList.toggle('selected', selected);
    if (selected) tab.setAttribute('aria-current', 'page');
    else tab.removeAttribute('aria-current');
  }
  if (page === 'history') loadHistory(offset);
  else if (page === 'shift') loadMovements(0);
  else if (page === 'expiry') loadExpiry(expiryOffset);
  else if (page === 'warehouse') warehouse.enter();
  else scanFocus();
}

function setBusy(value) {
  busy = value;
  renderRolePicker();
  renderStatus();
  renderBasketControls();
}

function renderStatus() {
  const inShift = active();
  const enabled = verified && !busy && !state.pending;
  $('register-label').textContent = connection ? t('Касса ') + connection.register.slice(0, 8) : t('Касса 01 · учебный кассир');
  $('mode-badge').textContent = connection ? t('◉ Учебный API · ') + connection.server : t('◉ Учебный режим');
  $('shift-badge').textContent = !verified ? t('◷ Проверить смену') : inShift ? t('● Смена открыта') : t('○ Смена закрыта');
  $('shift-badge').classList.toggle('green', inShift);
  $('shift-badge').classList.toggle('gold', !verified);
  $('sale-shift').textContent = inShift ? t('Текущая смена') : t('Открыть смену');
  $('sale-shift').disabled = !enabled;
  $('shift-action').textContent = inShift ? t('Закрыть смену') : t('Открыть смену');
  $('shift-action').disabled = !enabled;
  for (const id of ['cash-in', 'cash-out']) {
    $(id).hidden = !inShift;
    $(id).disabled = !enabled;
  }
  $('movement-panel').hidden = !state.shift;
  $('refresh-movements').disabled = busy;
  $('previous-movements').disabled = busy || movementOffset === 0;
  $('next-movements').disabled = busy || movementCount < pageSize || movementOffset >= 10000;
  $('refresh-expiry').disabled = busy;
  $('previous-expiry').disabled = busy || expiryOffset === 0;
  $('next-expiry').disabled = busy || expiryCount < expiryPageSize || expiryOffset >= 10000;
  for (const button of document.querySelectorAll('[data-expiry]')) button.disabled = busy;
  $('shift-status').textContent = inShift ? t('Смена открыта') : t('Смена закрыта');
  const shift = state.shift;
  $('metric-opening').textContent = shift ? formatMoney(apiMoney(shift.openingCash)) : '—';
  $('metric-sales').textContent = shift?.salesCount ?? '—';
  $('metric-cash').textContent = shift?.expectedCash != null ? formatMoney(apiMoney(shift.expectedCash)) : '—';
  $('metric-cash-unit').textContent = shift && shift.expectedCash == null && !shift.closedAt ? t('Доступно после закрытия') : t('сум');
  $('shift-detail').textContent = shift?.closedAt
    ? t('Пересчитано ') + formatMoney(apiMoney(shift.countedCash)) + t(' сум. Расхождение ') + formatMoney(Math.round(Number(shift.cashDifference) * 100)) + t(' сум.')
    : shift ? t('Открыта ') + dateTime.format(new Date(shift.openedAt)) + t(' по Ташкенту.') : t('Укажите стартовый размен, чтобы начать продажи.');
  $('search').disabled = !canQueueSaleInput();
  $('search-button').disabled = $('search').disabled;
  renderQuickControls();
  $('connect').disabled = busy || Boolean(searches) || !bridge;
  $('device-printers').disabled = busy || Boolean(searches) || !bridge;
  $('device-driver-actions').disabled = $('device-printers').disabled;
  $('connect').textContent = connection ? t('Обновить сеанс') : t('Подключить учебный стенд');
  $('disconnect').hidden = !connection;
  $('disconnect').disabled = busy || Boolean(state.pending);
  $('demo-settings').hidden = Boolean(connection);
  $('settings-state').textContent = connection
    ? t('Подключён учебный API. ID кассы ') + connection.register + '.'
    : t('Локальный учебный режим работает без интернета. При подключении к стенду следуйте подсказкам окна входа.');
  const pending = state.pending;
  const approval = pending?.approval;
  const shelf = pending?.kind === 'shelf';
  const waiting = approval === 'required';
  $('pending').hidden = !pending;
  $('check-operation').hidden = shelf || waiting || approval === 'approved';
  $('check-operation').disabled = busy;
  $('repeat-operation').hidden = !(canRepeat || (shelf && pending) || approval === 'approved');
  $('repeat-operation').disabled = busy;
  $('repeat-operation').textContent = shelf ? t('Подтвердить той же заявкой') : approval === 'approved'
    ? t('Завершить возврат') : t('Повторить ту же операцию');
  $('approve-refund').hidden = !waiting;
  $('approve-refund').disabled = busy;
  $('approve-refund').textContent = connection ? t('Подтверждение администратора') : t('Учебное подтверждение');
  $('cancel-approval').hidden = !waiting;
  $('cancel-approval').disabled = busy;
  if (pending) {
    $('pending-text').textContent = busy ? t('Проверяем операцию…') : waiting
      ? t('Нужно подтверждение администратора. К выдаче ') + formatMoney(pending.total) + t(' сум. Запрос ') + pending.body.operationId.slice(0, 8) + '.'
      : approval === 'approved' ? t('Администратор подтвердил возврат. Завершите его перед выдачей денег.')
      : shelf ? t('Результат отправки неясен. Для подтверждения отправьте ту же заявку.')
      : canRepeat ? t('Подтверждение пока не найдено. Можно повторить запрос с тем же идентификатором.')
      : t('Операция ожидает подтверждения. Проверьте статус перед следующей продажей.');
  }
  $('pay').disabled = !canPay();
  $('clear-basket').disabled = !canEdit() || !state.basket.length || Boolean(state.cart) || Boolean(searches);
  $('park-current').hidden = $('parked-carts').hidden = !connection;
  $('park-current').disabled = !canEdit() || !state.basket.length || Boolean(searches);
  $('park-current').textContent = state.cart ? t('Вернуть в отложенные') : t('Отложить чек');
  $('parked-carts').disabled = !canEdit() || Boolean(searches);
  $('restored-cart').hidden = !state.cart;
  $('refresh-history').disabled = busy;
  $('previous-history').disabled = busy || offset === 0;
  $('next-history').disabled = busy || historyCount < pageSize || offset >= 10000;
}

function renderBasketControls() {
  const locked = !canEdit() || Boolean(state.cart) || Boolean(searches);
  const stepLocked = !canQueueSaleInput();
  for (const control of $('basket').querySelectorAll('input,button'))
    control.disabled = control.classList.contains('quantity-step') ? stepLocked || control.dataset.limit === 'true' : locked;
}

function emptyRow(columns, message) {
  const row = element('tr');
  const cell = element('td', message, 'empty');
  cell.colSpan = columns;
  row.append(cell);
  return row;
}

function renderBasket() {
  const rows = state.basket.map((item, index) => {
    const row = element('tr');
    const name = element('td', item.name);
    const lot = item.expiryDate ? t(' · срок ') + item.expiryDate : '';
    name.append(element('small', (item.barcode || lotLabel(item.lotId)) + lot, 'mini'));
    if (item.promotionVersion) name.append(element('small', t('Акция 2+1'), 'mini'));
    if (item.lotPriceVersion) name.append(element('small', t('Уценка партии'), 'mini'));
    if (item.priceLabelId) name.append(element('small', t('Вес с этикетки'), 'mini'));
    const qtyCell = element('td');
    const input = element('input');
    input.value = String(item.units / 1000).replace('.', ',');
    input.inputMode = 'decimal';
    input.readOnly = Boolean(item.priceLabelId);
    input.maxLength = 18;
    input.setAttribute('aria-label', t('Количество · ') + item.name);
    input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); input.blur(); } });
    input.addEventListener('change', async () => {
      if (input.readOnly) return;
      const units = item.unit === 'kg' ? scaleQuantity(input.value) : quantity(input.value);
      if (!units || units > item.available || (item.unit === 'piece' && units % 1000) ||
          !Number.isFinite(lineTotal(item.price, units)) || lineTotal(item.price, units) === 0) {
        input.value = String(item.units / 1000).replace('.', ',');
        notice($('alert'), t('Укажите допустимое количество в пределах остатка партии.'));
        return;
      }
      await changeBasket(state.basket.map((entry, i) => i === index ? {...entry, units} : entry));
    });
    if (item.unit === 'piece' && !item.priceLabelId) {
      const controls = element('div', null, 'quantity-controls');
      const [minus, plus] = [
        [-1000, '−', t('Уменьшить на 1 · ')], [1000, '+', t('Увеличить на 1 · ')],
      ].map(([step, symbol, label]) => {
        const button = element('button', symbol, 'button outline quantity-step');
        button.type = 'button';
        button.setAttribute('aria-label', label + item.name);
        if (step > 0 && (item.units + step > item.available || !Number.isFinite(lineTotal(item.price, item.units + step)))) {
          button.dataset.limit = 'true';
          button.title = t('Укажите допустимое количество в пределах остатка партии.');
        }
        button.addEventListener('click', () => enqueue('quantityStep', {key: lotKey(item), step}));
        return button;
      });
      controls.append(minus, input, plus);
      qtyCell.append(controls);
    } else qtyCell.append(input);
    const remove = element('button', '×', 'icon-button');
    remove.type = 'button';
    remove.setAttribute('aria-label', t('Удалить · ') + item.name);
    remove.addEventListener('click', () => changeBasket(state.basket.filter((_, i) => i !== index)));
    const removeCell = element('td');
    removeCell.append(remove);
    row.append(element('td', index + 1), name, qtyCell, element('td', formatMoney(item.price), 'numeric'),
      element('td', formatMoney(lineTotal(item.price, item.units)), 'numeric'), removeCell);
    return row;
  });
  $('basket').replaceChildren(...(rows.length ? rows : [emptyRow(6, t('Корзина пуста. Добавьте первый товар.'))]));
  $('total').textContent = formatMoney(basketTotal(state.basket));
  const discount = basketDiscount(state.basket);
  $('promotion-summary').hidden = discount === 0;
  $('promotion-amount').textContent = '−' + formatMoney(discount) + t(' сум');
  $('summary-count').textContent = rows.length;
  $('item-count').textContent = t('Строк: ') + rows.length;
  renderBasketControls();
  renderStatus();
}

async function changeBasket(basket) {
  if (!canEdit() || state.cart) return false;
  setBusy(true);
  notice($('alert'));
  try {
    await save({...state, basket});
    renderBasket();
    return true;
  } catch (error) {
    notice($('alert'), describe(error));
    renderBasket();
    return false;
  } finally {
    setBusy(false);
    scanFocus();
  }
}

function displayResults(title, values) {
  $('result-title').textContent = title;
  $('result-items').replaceChildren(...values);
  $('results').hidden = false;
}

function resultRow(name, detail, buttonText, onClick) {
  const result = element('div', null, 'result');
  const label = element('div');
  label.append(element('strong', name), element('small', detail, 'mini'));
  const button = element('button', buttonText, 'button outline');
  button.type = 'button';
  button.addEventListener('click', onClick);
  result.append(label, button);
  return result;
}

function showProducts(products) {
  if (!Array.isArray(products)) throw new RequestError(0, 'invalid_response');
  const items = products.map(product => {
    const row = resultRow(product.name,
      [product.barcode || product.sku, product.unitPrice != null ? formatMoney(apiMoney(product.unitPrice)) + t(' сум') :
        t('Цена не установлена')].filter(Boolean).join(' · '),
      t('Выбрать'), () => enqueue('product', product.productId));
    if (product.unit === 'piece' && guidPattern.test(product.productId)) {
      const actions = element('div', null, 'product-result-actions');
      actions.append(row.lastElementChild);
      const pinned = quickProducts.some(item => item.id === product.productId);
      const pin = element('button', t(pinned ? 'Закреплено' : 'Закрепить'), 'button outline');
      pin.type = 'button';
      pin.dataset.quickPin = product.productId;
      pin.disabled = pinned;
      pin.addEventListener('click', () => {
        if (!canEdit() || searches || state.cart) return;
        if (quickProducts.some(item => item.id === product.productId)) return;
        if (quickProducts.length >= quickLimit) {
          notice($('alert'), t('Можно закрепить до восьми товаров. Сначала открепите один из них.'));
          return;
        }
        if (saveQuickProducts([...quickProducts, {id: product.productId, name: product.name}])) {
          pin.disabled = true;
          pin.textContent = t('Закреплено');
          toast(t('Товар закреплён на этой кассе.'));
          scanFocus();
        }
      });
      actions.append(pin);
      row.append(actions);
    }
    return row;
  });
  displayResults(t('Результаты поиска'), items.length ? items : [element('p', t('Подходящих товаров нет.'), 'empty')]);
}

function quickKey() {
  return 'kasa-quick-products-v1:' + (connection ?
    JSON.stringify([connection.endpoint ?? connection.server, connection.register]) : 'demo');
}

function loadQuickProducts() {
  try {
    const saved = JSON.parse(localStorage.getItem(quickKey()));
    quickProducts = Array.isArray(saved) ? saved.filter(item => guidPattern.test(item?.id ?? '') &&
      typeof item.name === 'string' && item.name.length > 0 && item.name.length <= 200).slice(0, quickLimit) : [];
  } catch { quickProducts = []; }
  quickEditing = false;
  renderQuickProducts();
}

function saveQuickProducts(next) {
  try { localStorage.setItem(quickKey(), JSON.stringify(next)); }
  catch {
    notice($('alert'), t('Не удалось сохранить быстрые товары на этой кассе.'));
    return false;
  }
  quickProducts = next;
  if (!next.length) quickEditing = false;
  renderQuickProducts();
  return true;
}

function renderQuickControls() {
  $('quick-edit').disabled = busy || Boolean(searches || state.pending);
  for (const control of $('quick-items').querySelectorAll('.quick-add'))
    control.disabled = quickEditing || $('search').disabled;
  for (const control of $('quick-items').querySelectorAll('.quick-remove'))
    control.disabled = $('quick-edit').disabled;
}

function renderQuickProducts() {
  $('quick-edit').hidden = !quickProducts.length;
  $('quick-edit').textContent = quickEditing ? t('Готово') : t('Настроить');
  $('quick-empty').hidden = Boolean(quickProducts.length);
  $('quick-items').hidden = !quickProducts.length;
  $('quick-items').replaceChildren(...quickProducts.map(item => {
    const tile = element('div', null, 'quick-tile');
    const button = element('button', null, 'button outline quick-add');
    button.type = 'button';
    const step = element('span', '+1', 'quick-step');
    step.setAttribute('aria-hidden', 'true');
    button.append(step, item.name);
    button.addEventListener('click', () => enqueue('quickProduct', item.id));
    tile.append(button);
    if (quickEditing) {
      const remove = element('button', '×', 'icon-button quick-remove');
      remove.type = 'button';
      remove.setAttribute('aria-label', t('Открепить · ') + item.name);
      remove.addEventListener('click', () => {
        if ($('quick-edit').disabled) return;
        if (saveQuickProducts(quickProducts.filter(row => row.id !== item.id))) {
          if (quickEditing) $('quick-edit').focus();
          else scanFocus();
          const pin = Array.from($('result-items').querySelectorAll('[data-quick-pin]'))
            .find(control => control.dataset.quickPin === item.id);
          if (pin) { pin.disabled = false; pin.textContent = t('Закрепить'); }
        }
      });
      tile.append(remove);
    }
    return tile;
  }));
  renderQuickControls();
}

function lotPricing(product, lot) {
  return {
    price: apiMoney(lot?.markdownUnitPrice ?? product?.unitPrice),
    promotionVersion: lot?.lotPriceVersion ? null : product?.promotionVersion ?? null,
    lotPriceVersion: lot?.lotPriceVersion ?? null,
  };
}

function showLotDialog(product, fromBarcode) {
  lotProduct = product;
  const defaultUnits = fromBarcode ? apiQuantity(product.quantityFactor ?? 1) : 1000;
  $('lot-product').textContent = product.name;
  $('lot-title').textContent = product.lots.length > 1 || product.requiresLotVerification ? t('Выберите партию') : t('Укажите количество');
  $('lot-warning').hidden = !product.requiresLotVerification && product.lots.length < 2;
  $('lot-qty-label').textContent = product.unit === 'kg' ? t('Вес, кг') : t('Количество, шт.');
  $('lot-quantity').value = product.unit === 'kg' && defaultUnits === 1000 ? '' : String(defaultUnits / 1000).replace('.', ',');
  const options = product.lots.map(lot => {
    const part = element('option', (lot.expiryDate ? t('Срок ') + lot.expiryDate : t('Без срока')) + t(' · остаток ') +
      lot.quantity + ' · ' + lotLabel(lot.lotId) + ' · ' + formatMoney(lotPricing(product, lot).price) + t(' сум') +
      (lot.lotPriceVersion ? ' · ' + t('Уценка партии') : ''));
    part.value = lot.lotId;
    return part;
  });
  const choose = element('option', t('Сверьте упаковку и выберите партию'));
  choose.value = '';
  choose.disabled = true;
  const deliberate = product.lots.length > 1 || product.requiresLotVerification;
  $('lot-select').replaceChildren(...(deliberate ? [choose, ...options] : options));
  if (deliberate) $('lot-select').value = '';
  notice($('lot-error'));
  $('lot-dialog').showModal();
  (deliberate ? $('lot-select') : $('lot-quantity')).focus();
}

function readPriceLabel(product, barcode) {
  const label = product.priceLabel;
  const lot = product.lots?.[0];
  const units = apiQuantity(label?.weight);
  const printed = apiMoney(label?.printedTotal);
  const price = lotPricing(product, lot).price;
  if (product.unit !== 'kg' || product.lots?.length !== 1 || !lot ||
      !label?.labelId || label.productId !== product.productId || label.lotId !== lot.lotId ||
      priceLabelBarcode(label.labelId).toUpperCase() !== barcode.toUpperCase() ||
      !units || !printed || !price || units > apiQuantity(lot.quantity))
    throw new RequestError(0, 'invalid_response');
  const current = lineTotal(price, units);
  if (!Number.isFinite(current) || !current) throw new RequestError(0, 'invalid_response');
  const conflict = current === printed ? '' : [
    t('На этикетке') + ' ' + formatMoney(printed) + t(' сум'),
    t('Текущая сумма') + ' ' + formatMoney(current) + t(' сум'),
    t('Продажа по этой этикетке заблокирована. Обратитесь к администратору за новой этикеткой.'),
  ].join(' · ');
  return {lot, units, conflict};
}

async function describeCheckoutError(error) {
  if (['price_changed', 'lot_price_changed', 'weighed_price_label_changed', 'parked_cart_version_conflict'].includes(error.code)) {
    for (const item of state.basket.filter(row => row.priceLabelId)) {
      try {
        const barcode = priceLabelBarcode(item.priceLabelId);
        const {conflict} = readPriceLabel(await call('barcode', barcode), barcode);
        if (conflict) return item.name + ' · ' + conflict;
      } catch { /* Keep the original checkout error if the current label cannot be read. */ }
    }
  }
  return describe(error);
}

async function addProduct(product, lot, units) {
  const available = apiQuantity(lot.quantity);
  const pricing = lotPricing(product, lot);
  const {price, promotionVersion, lotPriceVersion} = pricing;
  const index = state.basket.findIndex(item => item.productId === product.productId && item.lotId === lot.lotId);
  const existing = state.basket[index];
  const priceLabelId = product.priceLabel?.labelId;
  if (existing && (existing.priceLabelId || priceLabelId))
    throw new Error(t('Эта партия уже в чеке. Проведите другую упаковку из неё отдельным чеком.'));
  const productLines = state.basket.filter(item => item.productId === product.productId);
  if (productLines.some(item => item.priceVersion !== product.priceVersion)) throw new RequestError(409, 'price_changed');
  if (existing && ((existing.lotPriceVersion ?? null) !== lotPriceVersion || existing.price !== price))
    throw new RequestError(409, 'lot_price_changed');
  if (!lotPriceVersion && productLines.some(item => !item.lotPriceVersion && (item.promotionVersion ?? null) !== promotionVersion))
    throw new RequestError(409, 'quantity_promotion_changed');
  if (!units || (product.unit === 'piece' && units % 1000) || !price) throw new Error(t('Укажите допустимое количество.'));
  const newUnits = (existing?.units ?? 0) + units;
  if (!available || newUnits > available) throw new Error(t('Количество превышает остаток партии.'));
  if (index < 0 && state.basket.length >= 100) throw new Error(t('В одном чеке может быть до 100 строк.'));
  const line = lineTotal(price, newUnits);
  if (!Number.isFinite(line) || line <= 0) throw new Error(t('Недопустимая сумма позиции.'));
  const next = {productId: product.productId, lotId: lot.lotId, priceVersion: product.priceVersion,
    name: product.name, barcode: product.barcode, unit: product.unit, expiryDate: lot.expiryDate, available,
    units: newUnits, ...pricing, ...(priceLabelId ? {priceLabelId} : {})};
  const basket = index >= 0 ? state.basket.map((row, i) => i === index ? next : row) : [...state.basket, next];
  if (!await changeBasket(basket)) throw new RequestError(0, 'local_storage_error');
  hideResults();
  toast(t('Добавлено · ') + product.name);
}

async function stepQuantity({key, step}) {
  const item = state.basket.find(row => lotKey(row) === key);
  if (!item || item.unit !== 'piece' || item.priceLabelId) return;
  const units = item.units + step;
  if (units === 0) {
    await changeBasket(state.basket.filter(row => row !== item));
    return;
  }
  if (units > item.available || !Number.isFinite(lineTotal(item.price, units))) {
    notice($('alert'), t('Укажите допустимое количество в пределах остатка партии.'));
    return;
  }
  await changeBasket(state.basket.map(row => row === item ? {...row, units} : row));
}

function enqueue(action, value) {
  if (!canQueueSaleInput() || !value) return;
  if (searches >= scanQueueLimit) { toast(t('Очередь сканирования заполнена. Подождите.')); return; }
  searches += 1;
  renderStatus();
  renderBasketControls();
  searchQueue = searchQueue.then(async () => {
    if (document.querySelector('dialog[open]')) {
      toast(t('Завершите выбор партии, затем повторите сканирование.'));
      return;
    }
    notice($('alert'));
    try {
      if (action === 'quantityStep') return await stepQuantity(value);
      const fromPriceLabel = action === 'barcode' && isPriceLabelBarcode(value);
      if (fromPriceLabel && !connection) {
        notice($('alert'), t('Для проверки весовой этикетки подключите стенд.'));
        return;
      }
      const quick = action === 'quickProduct';
      const result = await call(quick ? 'product' : action, value);
      if (quick && result?.productId !== value) throw new RequestError(0, 'invalid_response');
      if (quick && result.unit !== 'piece') {
        notice($('alert'), t('Быстрые кнопки доступны для штучных товаров.'));
        return;
      }
      if (quick && quickProducts.some(item => item.id === value && item.name !== result.name))
        saveQuickProducts(quickProducts.map(item => item.id === value ? {id: value, name: result.name} : item));
      if (fromPriceLabel) {
        const {lot, units, conflict} = readPriceLabel(result, value);
        if (conflict) notice($('alert'), result.name + ' · ' + conflict);
        else await addProduct(result, lot, units);
        return;
      }
      if (action === 'search') return showProducts(result);
      if (!Array.isArray(result?.lots)) throw new RequestError(0, 'invalid_response');
      if (!result.lots.length) { notice($('alert'), t('Нет доступных партий для продажи.')); return; }
      if (result.requiresLotVerification || result.lots.length !== 1 || result.unit === 'kg') showLotDialog(result, action === 'barcode');
      else await addProduct(result, result.lots[0], action === 'barcode' ? apiQuantity(result.quantityFactor ?? 1) : 1000);
    } catch (error) {
      if (action === 'barcode' && error.code === 'product_not_found') {
        $('search').value = value;
        $('search').select();
      }
      notice($('alert'), error instanceof RequestError ? describe(error) : error.message);
    } finally {
      scanFocus();
    }
  }).finally(() => {
    searches -= 1;
    renderStatus();
    renderBasketControls();
  });
}

async function openAmount(action) {
  if (busy || state.pending || !verified || searches) return;
  if (action === 'commit' && !canPay()) return;
  if (action === 'close' && state.basket.length) {
    notice($('alert'), t('Завершите продажу или очистите текущий чек перед закрытием смены.'));
    return;
  }
  if (action === 'commit' && connection) {
    setBusy(true);
    notice($('alert'));
    try {
      const shiftId = state.shift.shiftId;
      if (state.cart) {
        const {cart, lines} = await call('cartReview', shiftId, state.cart.id);
        const byLot = new Map((lines ?? []).map(line => [lotKey(line), line]));
        if (cart?.cartId !== state.cart.id || cart.shiftId !== shiftId || cart.status !== 'parked' ||
            cart.version !== state.cart.version || byLot.size !== state.basket.length ||
            state.basket.some(item => {
              const line = byLot.get(lotKey(item));
              return !line || !['ready', 'priceChanged'].includes(line.status) ||
                line.currentPriceVersion !== item.priceVersion || apiMoney(line.currentUnitPrice) !== item.price ||
                (line.currentPromotionVersion ?? null) !== (item.promotionVersion ?? null) ||
                (line.currentLotPriceVersion ?? null) !== (item.lotPriceVersion ?? null) ||
                apiQuantity(line.availableQuantity) < item.units;
            })) throw new RequestError(409, 'parked_cart_version_conflict');
      }
      const quote = await call('quote', shiftId, {items: saleItems()});
      if (apiMoney(quote?.total) !== basketTotal(state.basket) ||
          apiMoney(quote?.discountAmount) !== basketDiscount(state.basket))
        throw new RequestError(0, 'invalid_response');
    } catch (error) {
      if ([401, 403].includes(error.status) || ['shift_not_found', 'active_shift_required'].includes(error.code))
        verified = false;
      notice($('alert'), t('Оплата недоступна. Корзина сохранена. ') + await describeCheckoutError(error));
      return;
    } finally {
      setBusy(false);
      scanFocus();
    }
  }
  amountAction = action;
  const payment = action === 'commit';
  const opening = action === 'open';
  $('dialog-eyebrow').textContent = payment ? t('НАЛИЧНАЯ ОПЛАТА') : t('КАССОВАЯ СМЕНА');
  $('dialog-title').textContent = payment ? t('Оплата наличными') : opening ? t('Открыть смену') : t('Закрыть смену');
  $('payment-total').hidden = !payment;
  $('payment-amount').textContent = formatMoney(basketTotal(state.basket)) + t(' сум');
  $('amount-label').textContent = payment ? t('Покупатель внёс, сум') : opening ? t('Начальный размен, сум') : t('Фактически пересчитано, сум');
  $('amount-input').value = opening ? '0' : '';
  $('amount-confirm').textContent = payment ? t('Подтвердить оплату') : opening ? t('Открыть смену') : t('Закрыть смену');
  notice($('amount-error'));
  updateAmountHint();
  $('amount-dialog').showModal();
  $('amount-input').select();
}

function updateAmountHint() {
  const entered = money($('amount-input').value);
  const total = basketTotal(state.basket);
  $('amount-hint').textContent = amountAction === 'commit'
    ? (entered != null && entered >= total ? t('Сдача ') + formatMoney(entered - total) + t(' сум.') : t('Введите сумму, полученную от покупателя.'))
    : amountAction === 'close' && state.shift?.expectedCash != null
      ? t('По учёту ') + formatMoney(apiMoney(state.shift.expectedCash)) + t(' сум.')
      : amountAction === 'close' ? t('Укажите сумму по фактическому пересчёту.') : t('Для учебной смены можно указать 0.');
}

async function finish(pending, result) {
  if (!validateOutcome(pending, result)) throw new RequestError(0, 'invalid_response');
  const {kind, shiftId} = pending;
  let shift = state.shift;
  const clearsBasket = kind === 'commit' || kind === 'parkCart';
  const basket = clearsBasket ? [] : state.basket;
  if (kind === 'open' || kind === 'close') {
    shift = result;
    verified = true;
  } else if (kind !== 'shelf') {
    try {
      const updated = await call('shift', shiftId);
      if (updated?.shiftId !== shiftId) throw new RequestError(0, 'invalid_response');
      shift = updated;
      verified = true;
    } catch { verified = false; }
  }
  await save({...state, shift, basket, cart: clearsBasket ? null : state.cart, pending: null});
  canRepeat = false;
  if (kind === 'commit') showReceipt(result, true);
  if (kind === 'createRefund') showRefundReceipt(result, pending.labels);
  if (kind === 'open') toast(t('Смена открыта'));
  if (kind === 'close') toast(t('Смена закрыта'));
  if (kind === 'createCash') toast(t('Операция с наличными записана'));
  if (kind === 'parkCart') toast(result.status === 'parked' ? t('Чек отложен') : t('Сохранение сверено. Чек уже закрыт или истёк.'));
  if (kind === 'shelf') {
    shelfMarks.set(lotKey(result), result.createdAt);
    toast(t('Задача по партии зарегистрирована'));
  }
  if (!verified && kind !== 'shelf') notice($('alert'), t('Операция сохранена. Обновите сеанс, чтобы проверить смену.'));
}

async function resolveOperation(mode, fresh) {
  let pending = state.pending;
  if (!pending || busy) return;
  setBusy(true);
  notice($('alert'));
  const {kind, body, shiftId} = pending;
  let finished = false;
  try {
    let result = mode !== 'start' ? await findOutcome(call, pending) : null;
    if (!result) {
      if (mode === 'check') {
        canRepeat = true;
        return;
      }
      if (pending.approval) {
        pending = {...pending, approval: 'retrying'};
        await save({...state, pending});
      }
      try {
        result = await call(kind, shiftId, body);
      } catch (error) {
        if (uncertain(error)) {
          result = await findOutcome(call, pending);
          if (!result) { canRepeat = true; return; }
          toast(t('Операция сверена по исходному ID'));
        } else {
          if (error.code === 'refund_approval_required') {
            await save({...state, pending: {...pending, approval: 'required'}});
            canRepeat = false;
            return;
          }
          const retryable = [401, 403, 429].includes(error.status);
          const staleApprovedRefund = pending.approval && ['refund_quantity_exceeded', 'refund_approval_conflict',
            'active_shift_required', 'insufficient_cash_balance'].includes(error.code);
          if ((fresh && !retryable && !operationCollisions.has(error.code)) || staleApprovedRefund) {
            await save({...state, pending: null});
            canRepeat = false;
          } else canRepeat = true;
          throw error;
        }
      }
    }
    await finish(pending, result);
    finished = true;
  } catch (error) {
    if (error.status === 401 || error.status === 403) verified = false;
    notice($('alert'), kind === 'commit' ? await describeCheckoutError(error) : describe(error));
  } finally {
    setBusy(false);
    renderBasket();
    scanFocus();
    if (finished && page === 'shift') loadMovements(0);
    if (finished && page === 'history' && kind === 'createRefund') loadHistory(offset);
    if (finished && page === 'expiry' && kind === 'shelf') loadExpiry(expiryOffset);
  }
}

async function journalOperation(pending, dialog, errorNode) {
  if (busy || state.pending) return;
  setBusy(true);
  try {
    await save({...state, pending});
  } catch (error) {
    notice(errorNode, describe(error));
    return;
  } finally {
    setBusy(false);
  }
  dialog?.close();
  canRepeat = false;
  await resolveOperation('start', true);
}

function saleItems() {
  return state.basket.map(({productId, lotId, priceVersion, units, promotionVersion, lotPriceVersion, priceLabelId}) =>
    ({productId, lotId, priceVersion, quantity: units / 1000, ...(promotionVersion ? {promotionVersion} : {}),
      ...(lotPriceVersion ? {lotPriceVersion} : {}), ...(priceLabelId ? {priceLabelId} : {})}));
}

async function parkCurrent() {
  if (!connection || !canEdit() || !state.basket.length || searches) return;
  if (!state.cart) {
    await journalOperation({kind: 'parkCart', shiftId: state.shift.shiftId,
      body: {cartId: crypto.randomUUID(), expectedVersion: 0, items: saleItems()}}, null, $('alert'));
    return;
  }
  setBusy(true);
  try {
    await save({...state, basket: [], cart: null});
    renderBasket();
    toast(t('Чек снова в списке отложенных'));
  } catch (error) {
    notice($('alert'), describe(error));
  } finally {
    setBusy(false);
    scanFocus();
  }
}

async function listParkedCarts(nextOffset) {
  if (!connection || !canEdit() || searches) return;
  setBusy(true);
  notice($('cart-error'));
  const list = $('cart-list');
  list.replaceChildren(element('p', t('Загрузка…')));
  $('cart-prev').disabled = $('cart-next').disabled = true;
  if (!$('cart-dialog').open) $('cart-dialog').showModal();
  try {
    const rows = await call('parkedCarts', state.shift.shiftId, String(nextOffset));
    if (!Array.isArray(rows)) throw new RequestError(0, 'invalid_response');
    cartOffset = nextOffset;
    list.replaceChildren(...rows.map(cart => {
      const name = t('Чек ') + cart.cartId.slice(0, 8);
      const detail = dateTime.format(new Date(cart.createdAt)) + t(' · позиций ') + cart.items.length;
      const row = resultRow(name, detail, t('Восстановить'), () => restoreParkedCart(cart));
      const restore = row.querySelector('button');
      restore.disabled = Boolean(state.basket.length);
      if (restore.disabled) restore.title = t('Сначала завершите текущий чек.');
      const cancel = element('button', t('Отменить чек'), 'button outline');
      cancel.type = 'button';
      cancel.addEventListener('click', () => cancelParkedCart(cart));
      const actions = element('div', null, 'actions');
      restore.replaceWith(actions);
      actions.append(restore, cancel);
      return row;
    }));
    if (!rows.length) list.append(element('p', t('На этой странице нет отложенных чеков.'), 'empty'));
    $('cart-prev').disabled = nextOffset === 0;
    $('cart-next').disabled = rows.length < pageSize || nextOffset >= 10000;
  } catch (error) {
    list.replaceChildren();
    notice($('cart-error'), describe(error));
    $('cart-prev').disabled = nextOffset === 0;
  } finally { setBusy(false); }
}

async function restoreParkedCart(selected) {
  if (busy || state.basket.length) return;
  setBusy(true);
  notice($('cart-error'));
  try {
    const {cart, lines} = await call('cartReview', state.shift.shiftId, selected.cartId);
    if (cart?.cartId !== selected.cartId || cart.shiftId !== state.shift.shiftId ||
        cart.version !== selected.version || cart.status !== 'parked')
      throw new RequestError(409, 'parked_cart_version_conflict');
    if (!Array.isArray(cart.items) || !Array.isArray(lines) || lines.length !== cart.items.length || !lines.length)
      throw new RequestError(0, 'invalid_response');
    if (lines.some(line => line.status !== 'ready' && line.status !== 'priceChanged')) {
      notice($('cart-error'), t('Есть недоступные товары или партии. Проверьте наличие и соберите новый чек.'));
      return;
    }
    const products = new Map();
    const reviewed = new Map(lines.map(line => [lotKey(line), line]));
    const basket = [];
    const changes = [];
    const promotionsChanged = new Set();
    for (const item of cart.items) {
      let product;
      if (item.priceLabelId) {
        const barcode = priceLabelBarcode(item.priceLabelId);
        product = await call('barcode', barcode);
        const label = readPriceLabel(product, barcode);
        if (label.conflict) {
          notice($('cart-error'), product.name + ' · ' + label.conflict);
          return;
        }
        if (label.units !== apiQuantity(item.quantity)) throw new RequestError(0, 'invalid_response');
      } else {
        if (!products.has(item.productId)) products.set(item.productId, await call('product', item.productId));
        product = products.get(item.productId);
      }
      const line = reviewed.get(lotKey(item));
      const lot = product?.lots?.find(row => row.lotId === item.lotId);
      const pricing = lotPricing(product, lot);
      const {price, promotionVersion, lotPriceVersion} = pricing;
      const units = apiQuantity(item.quantity);
      const available = apiQuantity(lot?.quantity);
      const total = price && units ? lineTotal(price, units) : 0;
      if (!line || line.savedPriceVersion !== item.priceVersion || product?.productId !== item.productId ||
          product.priceVersion !== line.currentPriceVersion || price !== apiMoney(line.currentUnitPrice) ||
          promotionVersion !== (line.currentPromotionVersion ?? null) ||
          lotPriceVersion !== (line.currentLotPriceVersion ?? null) ||
          !price || !units || available < units || !Number.isFinite(total) || total <= 0)
        throw new RequestError(409, 'parked_cart_version_conflict');
      basket.push({productId: item.productId, lotId: item.lotId, priceVersion: product.priceVersion,
        name: product.name, barcode: product.barcode, unit: product.unit, expiryDate: lot.expiryDate, available, units,
        ...pricing, ...(item.priceLabelId ? {priceLabelId: item.priceLabelId} : {})});
      if (line.status === 'priceChanged') changes.push(product.name + ' · ' + formatMoney(price) + t(' сум'));
      if ((item.promotionVersion ?? null) !== promotionVersion) promotionsChanged.add(product.name);
    }
    if (changes.length || promotionsChanged.size) {
      const messages = [];
      if (changes.length) messages.push(t('Цена изменилась. Подтвердить новые цены?'), ...changes);
      if (promotionsChanged.size) messages.push(t('Условия акции 2+1 изменились. Подтвердить новый итог?'), ...promotionsChanged);
      messages.push('', t('Итого ') + formatMoney(basketTotal(basket)) + t(' сум'));
      if (!window.confirm(messages.join('\n'))) return;
    }
    await save({...state, basket, cart: {id: cart.cartId, version: cart.version}});
    hideResults();
    $('cart-dialog').close();
    renderBasket();
  } catch (error) {
    notice($('cart-error'), describe(error));
  } finally { setBusy(false); }
}

async function cancelParkedCart(cart) {
  if (busy || state.cart?.id === cart.cartId) {
    if (!busy) notice($('cart-error'), t('Сначала верните текущий чек в отложенные.'));
    return;
  }
  const reason = window.prompt(t('Укажите причину отмены отложенного чека.'));
  if (reason === null) return;
  if (!reason.trim() || reason.trim().length > 500) {
    notice($('cart-error'), t('Введите причину от 1 до 500 символов.'));
    return;
  }
  setBusy(true);
  try {
    const result = await call('cancelCart', state.shift.shiftId, {cartId: cart.cartId, expectedVersion: cart.version, reason: reason.trim()});
    if (result?.cartId !== cart.cartId || result.status !== 'cancelled' || result.version !== cart.version + 1)
      throw new RequestError(0, 'invalid_response');
  } catch (error) {
    notice($('cart-error'), describe(error));
    return;
  } finally { setBusy(false); }
  await listParkedCarts(cartOffset);
}

async function submitAmount(value) {
  const kind = amountAction;
  const shiftId = kind === 'open' ? crypto.randomUUID() : state.shift.shiftId;
  const body = kind === 'open' ? {shiftId, openingCash: value / 100}
    : kind === 'close' ? {countedCash: value / 100}
    : {operationId: crypto.randomUUID(), shiftId, method: 'cash', tendered: value / 100,
      items: saleItems(), ...(state.cart ? {cartId: state.cart.id, cartVersion: state.cart.version} : {})};
  if (kind === 'commit') $('simulate').checked = false;
  hideResults();
  const expectedTotal = kind === 'commit' ? basketTotal(state.basket) : undefined;
  await journalOperation({kind, shiftId, body, expectedTotal}, $('amount-dialog'), $('amount-error'));
}

function showReceipt(receipt, justSaved = false) {
  if (!Array.isArray(receipt?.lines) || !receipt.lines.length) throw new RequestError(0, 'invalid_response');
  $('receipt-title').textContent = justSaved ? t('Чек сохранён') : t('Просмотр чека');
  const list = element('table');
  const head = element('thead');
  const labels = element('tr');
  labels.append(element('th', t('Товар и количество')), element('th', t('Сумма'), 'numeric'));
  head.append(labels);
  const rows = element('tbody');
  for (const item of receipt.lines) {
    const row = element('tr');
    const label = element('td', item.name);
    label.append(element('small', String(item.quantity).replace('.', ',') + (item.unit === 'kg' ? t(' кг × ') : t(' шт. × ')) + formatMoney(apiMoney(item.unitPrice)), 'mini'));
    if (item.lotPriceVersion) label.append(element('small', t('Уценка партии'), 'mini'));
    if (item.priceLabelId) label.append(element('small', t('Вес с этикетки'), 'mini'));
    row.append(label, element('td', formatMoney(apiMoney(item.lineTotal)), 'numeric'));
    rows.append(row);
  }
  list.append(head, rows);
  const payment = element('div', null, 'payment-total');
  payment.append(element('span', t('Итого')), element('strong', formatMoney(apiMoney(receipt.total)) + t(' сум'), 'amount'));
  const metadata = element('p', t('Чек ') + receipt.operationId, 'receipt-id');
  const time = element('p', dateTime.format(new Date(receipt.createdAt)) + t(' · Ташкент'));
  const cash = element('p', receipt.method === 'cash'
    ? t('Наличные ') + formatMoney(apiMoney(receipt.tendered)) + t(' сум · сдача ') + formatMoney(apiMoney(receipt.changeDue)) + t(' сум')
    : t('Ручной безналичный расчёт'));
  const discount = apiMoney(receipt.discountAmount ?? 0);
  const discountRows = discount > 0 ? [element('p', t(receipt.discountReason === '2+1' ? 'Скидка 2+1' : 'Скидка') +
    ' −' + formatMoney(discount) + t(' сум'))] : [];
  $('receipt-paper').replaceChildren(element('h2', t('Касса минимаркета')), element('p', t(receiptMark), 'receipt-mark'),
    time, metadata, list, ...discountRows, payment, cash);
  notice($('print-error'));
  $('receipt-dialog').showModal();
}

async function loadHistory(requestedOffset) {
  const version = ++historyVersion;
  setBusy(true);
  $('history-rows').replaceChildren(emptyRow(5, t('Загрузка…')));
  try {
    const results = await call('history', String(requestedOffset), receiptQuery);
    if (version !== historyVersion || page !== 'history') return;
    if (!Array.isArray(results)) throw new RequestError(0, 'invalid_response');
    offset = requestedOffset;
    historyCount = results.length;
    $('history-count').textContent = historyCount ? (offset + 1) + '–' + (offset + historyCount) : t('Нет чеков');
    const rows = results.map(entry => {
      const id = entry.receiptId;
      const row = element('tr');
      const open = element('button', t('Открыть'), 'button outline');
      open.addEventListener('click', async () => {
        if (busy) return;
        setBusy(true);
        notice($('alert'));
        try { showReceipt(await call('sale', id)); }
        catch (error) { notice($('alert'), describe(error)); }
        finally { setBusy(false); }
      });
      const cell = element('td');
      const actions = element('div', null, 'actions');
      actions.append(open);
      if (active() && entry.method === 'cash' && entry.refundStatus !== 'full') {
        const refund = element('button', t('Возврат'), 'button outline');
        refund.addEventListener('click', () => openRefund(entry));
        actions.append(refund);
      }
      cell.append(actions);
      const number = element('td', id.slice(0, 8));
      number.title = id;
      row.append(number, element('td', dateTime.format(new Date(entry.createdAt))),
        element('td', formatMoney(apiMoney(entry.total)), 'numeric'),
        element('td', entry.refundStatus === 'full' ? t('Полный') : entry.refundStatus === 'partial'
          ? t('Частичный · ') + formatMoney(apiMoney(entry.refundedAmount)) + t(' сум') : '—'), cell);
      return row;
    });
    $('history-rows').replaceChildren(...(rows.length ? rows : [emptyRow(5, t('На этой странице чеков нет.'))]));
  } catch (error) {
    historyCount = 0;
    $('history-count').textContent = t('Ошибка');
    $('history-rows').replaceChildren(emptyRow(5, describe(error)));
  } finally {
    setBusy(false);
  }
}

async function refreshShift() {
  verified = !state.shift || Boolean(state.shift.closedAt);
  if (verified) return;
  const shift = await call('shift', state.shift.shiftId);
  if (shift?.shiftId !== state.shift.shiftId) throw new RequestError(0, 'invalid_response');
  if (shift.closedAt && state.basket.length) {
    await save({...state, shift, basket: [], cart: null});
    toast(t('Смена была закрыта на сервере. Черновик чека сброшен.'));
  } else await save({...state, shift});
  verified = true;
}

async function switchMode(online) {
  if (busy || searches || (!online && state.pending)) return;
  setBusy(true);
  notice($('alert'));
  try {
    if (online) {
      const attached = await bridge('connect');
      if (!attached) return;
      connection = attached;
      applyRoleSession(attached.roles, 'cashier');
      loadQuickProducts();
      setApiState('online');
      verified = false;
      state = emptyState();
      state = decodeState(await bridge('load'));
    } else {
      await bridge('disconnect');
      connection = null;
      availableRoles = ['cashier', 'receiver', 'admin'];
      ownerRole = false;
      activeRole = 'cashier';
      renderRolePicker();
      loadQuickProducts();
      setApiState(null);
      state = decodeState(localStorage.getItem(demoKey));
    }
    offset = historyCount = expiryOffset = movementOffset = 0;
    receiptQuery = '';
    $('receipt-query').value = '';
    shelfMarks.clear();
    canRepeat = false;
    hideResults();
    if (!state.pending) await refreshShift();
  } catch (error) {
    if (connection) recordApiError(error);
    notice($('alert'), describe(error));
  } finally {
    setBusy(false);
    renderBasket();
  }
}

const guidPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const displayUnits = (value, unit) => String(value).replace('.', ',') + (unit === 'kg' ? t(' кг') : t(' шт.'));
const shortDate = value => value ? value.split('-').reverse().join('.') : '—';

function showRefundReceipt(result, labels = []) {
  $('receipt-title').textContent = t('Возврат сохранён');
  const list = element('table');
  const rows = element('tbody');
  for (const item of result.lines) {
    const row = element('tr');
    const meta = labels.find(line => lotKey(line) === lotKey(item));
    const name = element('td', meta?.name ?? t('Партия ') + lotLabel(item.lotId));
    name.append(element('small', displayUnits(item.quantity, meta?.unit) + ' · ' +
      (item.destination === 'quarantine' ? t('Карантин') : t('На остаток')), 'mini'));
    row.append(name, element('td', formatMoney(apiMoney(item.amount)), 'numeric'));
    rows.append(row);
  }
  list.append(rows);
  const payment = element('div', null, 'payment-total');
  payment.append(element('span', t('К выдаче')), element('strong', formatMoney(apiMoney(result.total)) + t(' сум'), 'amount'));
  $('receipt-paper').replaceChildren(element('h2', t('Касса минимаркета · Возврат')), element('p', t(receiptMark), 'receipt-mark'),
    element('p', dateTime.format(new Date(result.createdAt)) + t(' · Ташкент')),
    element('p', t('Возврат ') + result.operationId, 'receipt-id'), element('p', t('Исходный чек ') + result.saleId, 'receipt-id'),
    element('p', result.reason), list, payment);
  notice($('print-error'));
  $('receipt-dialog').showModal();
}

async function openRefund(entry) {
  if (busy || state.pending || !active()) return;
  setBusy(true);
  notice($('alert'));
  try {
    const registerId = entry.registerId ?? currentRegister();
    const response = await call('refundOptions', entry.receiptId, registerId);
    if (response?.saleId !== entry.receiptId || response.sourceRegisterId !== registerId || !Array.isArray(response.lines))
      throw new RequestError(0, 'invalid_response');
    if (response.method !== 'cash') throw new RequestError(400, 'cash_only');
    const lines = response.lines.filter(line => apiQuantity(line.soldQuantity) > apiQuantity(line.returnedQuantity));
    if (!lines.length) {
      notice($('alert'), t('Все позиции чека уже возвращены.'));
      return;
    }
    const fields = lines.map((line, index) => {
      const row = element('tr');
      const name = element('td', line.name);
      name.append(element('small', t('Партия ') + lotLabel(line.lotId), 'mini'));
      const available = (apiQuantity(line.soldQuantity) - apiQuantity(line.returnedQuantity)) / 1000;
      const input = element('input');
      input.inputMode = 'decimal';
      input.maxLength = 18;
      input.placeholder = '0';
      input.setAttribute('aria-label', t('К возврату · ') + line.name + ' · ' + (index + 1));
      input.addEventListener('input', updateRefund);
      const destination = element('select');
      destination.setAttribute('aria-label', t('Куда вернуть · ') + line.name + ' · ' + (index + 1));
      for (const [value, label] of [['quarantine', t('В карантин')], ['stock', t('На остаток')]]) {
        const option = element('option', label);
        option.value = value;
        destination.append(option);
      }
      const quantityCell = element('td');
      quantityCell.append(element('label', null, 'field'));
      quantityCell.firstChild.append(input);
      const destinationCell = element('td');
      destinationCell.append(element('label', null, 'field'));
      destinationCell.firstChild.append(destination);
      row.append(name, element('td', displayUnits(available, line.unit)), quantityCell, destinationCell);
      return {line, input, destination, row};
    });
    refundDraft = {saleId: entry.receiptId, registerId, fields};
    $('refund-rows').replaceChildren(...fields.map(field => field.row));
    $('refund-sale').textContent = t('Исходный чек ') + entry.receiptId;
    $('refund-reason').value = '';
    updateRefund();
    $('refund-dialog').showModal();
    fields[0].input.focus();
  } catch (error) {
    notice($('alert'), describe(error));
  } finally {
    setBusy(false);
  }
}

function refundSelection() {
  let total = 0;
  const items = [];
  for (const {line, input, destination} of refundDraft.fields) {
    const units = input.value.trim() ? quantity(input.value) : 0;
    const available = apiQuantity(line.soldQuantity) - apiQuantity(line.returnedQuantity);
    if (units == null || units > available || (line.unit === 'piece' && units % 1000)) {
      return {error: t('Проверьте количество для позиции «') + line.name + '».'};
    }
    if (units) {
      total += refundCents(line, units);
      items.push({productId: line.productId, lotId: line.lotId, quantity: units / 1000, destination: destination.value});
    }
  }
  return {items, total};
}

function updateRefund() {
  const {items, total, error} = refundSelection();
  $('refund-confirm').disabled = !items?.length;
  $('refund-total').textContent = error ? '—' : formatMoney(total) + t(' сум');
  notice($('refund-error'), error);
}

async function approveRefund() {
  const pending = state.pending;
  if (busy || pending?.approval !== 'required') return;
  setBusy(true);
  notice($('alert'));
  try {
    const payload = {registerId: currentRegister(), amount: pending.total / 100, refund: pending.body};
    const result = await call('approveRefund', null, payload);
    if (!result) return;
    if (result.operationId !== pending.body.operationId || result.registerId !== currentRegister() ||
        apiMoney(result.amount) !== pending.total || !result.approvedBy)
      throw new RequestError(0, 'invalid_response');
    await save({...state, pending: {...pending, approval: 'approved'}});
  } catch (error) {
    const message = error.status === 401 ? t('Токен администратора просрочен или отклонён.') : describe(error);
    notice($('alert'), message);
  } finally {
    setBusy(false);
  }
}

async function cancelApproval() {
  if (busy || state.pending?.approval !== 'required') return;
  setBusy(true);
  try {
    await save({...state, pending: null});
    canRepeat = false;
    toast(t('Запрос на возврат отменён'));
  } catch (error) {
    notice($('alert'), describe(error));
  } finally {
    setBusy(false);
  }
}

async function loadMovements(requestedOffset) {
  if (busy || !state.shift) return;
  setBusy(true);
  $('movement-rows').replaceChildren(emptyRow(4, t('Загрузка…')));
  try {
    const entries = await call('cashMovements', state.shift.shiftId, String(requestedOffset));
    if (!Array.isArray(entries)) throw new RequestError(0, 'invalid_response');
    movementOffset = requestedOffset;
    movementCount = entries.length;
    const rows = entries.map(entry => {
      const row = element('tr');
      row.append(element('td', dateTime.format(new Date(entry.createdAt))),
        element('td', entry.direction === 'in' ? t('Внесение') : t('Изъятие')), element('td', entry.reason),
        element('td', (entry.direction === 'in' ? '+' : '−') + formatMoney(apiMoney(entry.amount)), 'numeric'));
      return row;
    });
    $('movement-rows').replaceChildren(...(rows.length ? rows : [emptyRow(4, t('Операций на этой странице пока нет.'))]));
  } catch (error) {
    movementCount = 0;
    $('movement-rows').replaceChildren(emptyRow(4, describe(error)));
  } finally {
    setBusy(false);
  }
}

function openMovement(direction) {
  if (busy || state.pending || !active()) return;
  movementDirection = direction;
  $('movement-title').textContent = direction === 'in' ? t('Внести наличные') : t('Изъять наличные');
  $('movement-form').reset();
  notice($('movement-error'));
  $('movement-dialog').showModal();
}

async function loadExpiry(requestedOffset) {
  if (busy) return;
  setBusy(true);
  $('expiry-rows').replaceChildren(emptyRow(5, t('Загрузка…')));
  for (const button of document.querySelectorAll('[data-expiry]')) {
    const selected = button.dataset.expiry === expiryFilter;
    button.classList.toggle('selected', selected);
    button.setAttribute('aria-pressed', String(selected));
  }
  try {
    const entries = await call('expiry', expiryFilter, String(requestedOffset));
    if (!Array.isArray(entries)) throw new RequestError(0, 'invalid_response');
    expiryOffset = requestedOffset;
    expiryCount = entries.length;
    $('expiry-count').textContent = expiryCount ? (expiryOffset + 1) + '–' + (expiryOffset + expiryCount) : t('Нет партий');
    const rows = entries.map(lot => {
      const row = element('tr');
      const product = element('td', lot.name);
      product.append(element('small', (lot.barcode || t('Без штрихкода')) + t(' · партия ') + lotLabel(lot.lotId), 'mini'));
      const days = lot.daysRemaining;
      const status = days == null ? t('Нет даты') : days < 0 ? t('Просрочено') : days === 0 ? t('Сегодня') : t('Дней осталось ') + days;
      const stateCell = element('td');
      stateCell.append(element('span', status, 'badge ' + (days == null || days < 0 ? 'red' : days <= 3 ? 'gold' : 'green')));
      const action = element('td');
      const mark = element('button', t('Снято с полки'), 'button outline');
      mark.addEventListener('click', () => openShelf(lot));
      action.append(mark);
      const markedAt = shelfMarks.get(lotKey(lot));
      if (markedAt) action.append(element('small', t('Задача создана · ') + dateTime.format(new Date(markedAt)), 'mini'));
      row.append(product, element('td', shortDate(lot.expiryDate)), stateCell,
        element('td', displayUnits(lot.accountedQuantity, lot.unit), 'numeric'), action);
      return row;
    });
    $('expiry-rows').replaceChildren(...(rows.length ? rows : [emptyRow(5, t('По этому фильтру партий нет.'))]));
  } catch (error) {
    expiryCount = 0;
    $('expiry-count').textContent = t('Ошибка');
    $('expiry-rows').replaceChildren(emptyRow(5, describe(error)));
  } finally {
    setBusy(false);
  }
}

function openShelf(lot) {
  if (busy || state.pending) return;
  shelfLot = lot;
  $('shelf-detail').textContent = lot.name + t(' · партия ') + lotLabel(lot.lotId) + t(' · срок ') + shortDate(lot.expiryDate);
  $('shelf-label').textContent = lot.unit === 'kg' ? t('Снятый вес, кг') : t('Снятое количество, шт.');
  $('shelf-form').reset();
  notice($('shelf-error'));
  $('shelf-dialog').showModal();
}

$('history-search').addEventListener('submit', event => {
  event.preventDefault();
  if (busy) return;
  const query = $('receipt-query').value.trim().toLowerCase();
  if (query && !guidPattern.test(query)) return notice($('alert'), t('Введите полный ID чека из печатного документа.'));
  notice($('alert'));
  receiptQuery = query;
  loadHistory(0);
});
$('refund-form').addEventListener('submit', event => {
  event.preventDefault();
  if (!active() || !refundDraft) return;
  const {error, items, total} = refundSelection();
  const reason = $('refund-reason').value.trim();
  if (error || !items?.length || !reason) return notice($('refund-error'), error || t('Укажите позиции и причину возврата.'));
  const shiftId = state.shift.shiftId;
  const {saleId, registerId, fields} = refundDraft;
  const labels = fields.map(({line}) => ({productId: line.productId, lotId: line.lotId, name: line.name, unit: line.unit}));
  const body = {operationId: crypto.randomUUID(), shiftId, sourceRegisterId: registerId, saleId, reason, items};
  journalOperation({kind: 'createRefund', shiftId, body, total, labels}, $('refund-dialog'), $('refund-error'));
});
$('approve-refund').addEventListener('click', approveRefund);
$('cancel-approval').addEventListener('click', cancelApproval);
$('cash-in').addEventListener('click', () => openMovement('in'));
$('cash-out').addEventListener('click', () => openMovement('out'));
$('movement-form').addEventListener('submit', event => {
  event.preventDefault();
  if (!active()) return;
  const amount = money($('movement-amount').value);
  const reason = $('movement-reason').value.trim();
  if (!amount || !reason) return notice($('movement-error'), t('Укажите сумму больше нуля и причину.'));
  const body = {operationId: crypto.randomUUID(), direction: movementDirection, amount: amount / 100, reason};
  journalOperation({kind: 'createCash', shiftId: state.shift.shiftId, body}, $('movement-dialog'), $('movement-error'));
});
$('refresh-movements').addEventListener('click', () => loadMovements(0));
$('previous-movements').addEventListener('click', () => loadMovements(Math.max(0, movementOffset - pageSize)));
$('next-movements').addEventListener('click', () => loadMovements(movementOffset + pageSize));
$('refresh-expiry').addEventListener('click', () => loadExpiry(0));
$('previous-expiry').addEventListener('click', () => loadExpiry(Math.max(0, expiryOffset - expiryPageSize)));
$('next-expiry').addEventListener('click', () => loadExpiry(expiryOffset + expiryPageSize));
$('expiry-filters').addEventListener('click', event => {
  const filter = event.target.closest('[data-expiry]');
  if (!filter || busy) return;
  expiryFilter = filter.dataset.expiry;
  loadExpiry(0);
});
$('shelf-form').addEventListener('submit', event => {
  event.preventDefault();
  const units = quantity($('shelf-quantity').value);
  if (!units || units > apiQuantity(shelfLot.accountedQuantity) || (shelfLot.unit === 'piece' && units % 1000))
    return notice($('shelf-error'), t('Введите допустимое количество в пределах учётного остатка.'));
  const body = {taskId: crypto.randomUUID(), productId: shelfLot.productId, lotId: shelfLot.lotId, quantity: units / 1000};
  journalOperation({kind: 'shelf', shiftId: null, body}, $('shelf-dialog'), $('shelf-error'));
});

mountPilot({bridge, $, notice, element, setBusy});
const warehouse = mountWarehouse({bridge, $, element, notice, emptyRow,
  protectedMode:()=>storeProtected,
  onSession:current=>applyRoleSession(current.roles, current.role)});

function renderProtection() {
  $('protect-store-data').checked = storeProtected;
  $('protection-state').textContent = storeProtected ? t('● Защита включена') : t('◷ Изменения разрешены');
  $('protection-state').className = 'badge ' + (storeProtected ? 'green' : 'gold') + ' w-self';
  warehouse.setProtected();
}

$('protect-store-data').addEventListener('change', event => {
  if (!event.target.checked && !window.confirm(t('Отключить защиту? После этого администратор и приёмщик смогут изменять данные магазина.'))) {
    event.target.checked = true;
    return;
  }
  storeProtected = event.target.checked;
  localStorage.setItem(protectionKey, storeProtected ? 'on' : 'off');
  renderProtection();
});

$('active-role').addEventListener('change', async event => {
  const next = event.target.value;
  const previous = activeRole;
  if (!availableRoles.includes(next) || next === activeRole) return;
  event.target.disabled = true;
  try {
    if (next === 'cashier') {
      if (bridge && !connection) await switchMode(true);
      if (bridge && !connection) throw new Error('role_switch_cancelled');
      activeRole = next;
      showPage('sale');
    } else {
      const current = await warehouse.activate(next);
      if (!current) throw new Error('role_switch_cancelled');
      activeRole = next;
      showPage('warehouse');
    }
  } catch {
    activeRole = previous;
    toast(t('Переключение роли отменено.'));
  } finally { renderRolePicker(); }
});

document.querySelectorAll('[data-page]').forEach(tab => tab.addEventListener('click', () => showPage(tab.dataset.page)));
document.querySelectorAll('[data-dismiss]').forEach(button => button.addEventListener('click', () => { if (!busy) button.closest('dialog').close(); }));
document.querySelectorAll('dialog').forEach(dialog => dialog.addEventListener('close', scanFocus));
$('sale-shift').addEventListener('click', () => active() ? showPage('shift') : openAmount('open'));
$('shift-action').addEventListener('click', () => openAmount(active() ? 'close' : 'open'));
$('quick-edit').addEventListener('click', () => {
  quickEditing = !quickEditing;
  renderQuickProducts();
  if (!quickEditing) scanFocus();
});
$('hide-results').addEventListener('click', () => { hideResults(); scanFocus(); });
$('pay').addEventListener('click', () => openAmount('commit'));
$('park-current').addEventListener('click', parkCurrent);
$('parked-carts').addEventListener('click', () => listParkedCarts(0));
$('cart-prev').addEventListener('click', () => listParkedCarts(Math.max(0, cartOffset - pageSize)));
$('cart-next').addEventListener('click', () => listParkedCarts(cartOffset + pageSize));
$('clear-basket').addEventListener('click', () => $('confirm-dialog').showModal());
$('confirm-clear').addEventListener('click', () => { $('confirm-dialog').close(); changeBasket([]); });
$('amount-input').addEventListener('input', updateAmountHint);
$('check-operation').addEventListener('click', () => resolveOperation('check', false));
$('repeat-operation').addEventListener('click', () => resolveOperation('repeat', false));
$('simulate').addEventListener('change', event => { demo.loseNextSale = event.target.checked; });
$('connect').addEventListener('click', () => switchMode(true));
$('disconnect').addEventListener('click', () => switchMode(false));
$('refresh-history').addEventListener('click', () => loadHistory(0));
$('previous-history').addEventListener('click', () => loadHistory(Math.max(0, offset - pageSize)));
$('next-history').addEventListener('click', () => loadHistory(offset + pageSize));
$('print').addEventListener('click', async () => {
  notice($('print-error'));
  try { if (bridge) await bridge('print'); else window.print(); }
  catch { notice($('print-error'), t('Не удалось открыть окно печати. Чек сохранён и доступен в журнале.')); }
});
$('scan-form').addEventListener('submit', event => {
  event.preventDefault();
  const value = $('search').value.trim();
  if (!value || !active() || state.pending) return;
  $('search').value = '';
  if (/^(LOT|WPL)-/i.test(value) && !isSaleBarcode(value)) {
    notice($('alert'), /^WPL-/i.test(value) ? errors.invalid_weighed_price_label : errors.invalid_lot_barcode);
    return;
  }
  enqueue(isSaleBarcode(value) ? 'barcode' : 'search', value);
});
$('amount-form').addEventListener('submit', event => {
  event.preventDefault();
  if (busy || state.pending) return;
  const entered = money($('amount-input').value);
  if (entered == null) return notice($('amount-error'), t('Введите сумму с точностью до двух знаков после запятой.'));
  if (amountAction === 'commit' && entered < basketTotal(state.basket))
    return notice($('amount-error'), t('Внесённая сумма меньше итога чека.'));
  submitAmount(entered);
});
$('lot-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (!canEdit()) return;
  const lot = lotProduct.lots.find(item => item.lotId === $('lot-select').value);
  if (!lot) return notice($('lot-error'), t('Выберите партию.'));
  try {
    const raw = $('lot-quantity').value;
    await addProduct(lotProduct, lot, lotProduct.unit === 'kg' ? scaleQuantity(raw) : quantity(raw));
    $('lot-dialog').close();
  } catch (error) {
    notice($('lot-error'), error instanceof RequestError ? describe(error) : error.message);
  }
});
document.addEventListener('keydown', event => {
  if (event.key !== 'F2' && event.key !== 'F3') return;
  event.preventDefault();
  if (page !== 'sale' || document.querySelector('dialog[open]')) return;
  if (event.key === 'F2') openAmount('commit');
  else scanFocus();
});
for (const dialog of document.querySelectorAll('dialog')) {
  dialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
}

try {
  state = decodeState(localStorage.getItem(demoKey));
} catch {
  verified = false;
  notice($('alert'), describe(new RequestError(0, 'local_storage_error')));
}
loadQuickProducts();
renderRolePicker();
renderProtection();
renderBasket();
scanFocus();
