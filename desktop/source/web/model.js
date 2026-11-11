export const receiptMark = 'Тестовый • нефискальный';
const rubles = new Intl.NumberFormat('ru-RU', {maximumFractionDigits: 2});
const MAX_CENTS = 9_999_999_999_999;
const MAX_MILLI = 99_999_999_999_999;

export function formatMoney(cents) {
  return rubles.format(cents / 100);
}

function scaled(raw, digits, limit) {
  const match = /^(\d+)(?:[.,](\d+))?$/.exec(String(raw).trim());
  if (!match || (match[2]?.length ?? 0) > digits || match[1].length > 14) return null;
  const value = Number(match[1]) * 10 ** digits + Number((match[2] ?? '').padEnd(digits, '0'));
  return Number.isSafeInteger(value) && value <= limit ? value : null;
}

export const money = value => scaled(value, 2, MAX_CENTS);
export const quantity = value => scaled(value, 3, MAX_MILLI);

export function scaleQuantity(value) {
  const match = /^(\d+(?:[.,]\d+)?)\s*(kg|g|кг|г)?$/i.exec(String(value).trim());
  if (!match) return null;
  return /^(g|г)$/i.test(match[2] ?? '') ? scaled(match[1], 0, MAX_MILLI) : quantity(match[1]);
}
export const apiMoney = value => typeof value === 'number' ? money(value) : null;
export const apiQuantity = value => typeof value === 'number' ? quantity(value) : null;
export const isPriceLabelBarcode = value => /^WPL-[0-9a-f]{32}$/i.test(value);
export const priceLabelBarcode = id => 'WPL-' + id.replaceAll('-', '');
export const isSaleBarcode = value => /^(?:\d{1,48}|LOT-[0-9a-f]{32})$/i.test(value) || isPriceLabelBarcode(value);

export function lineTotal(price, units) {
  const result = (BigInt(price) * BigInt(units) + 500n) / 1000n;
  return result <= BigInt(MAX_CENTS) ? Number(result) : Infinity;
}

export function basketTotal(lines) {
  return lines.reduce((sum, row) => sum + lineTotal(row.price, row.units), 0) - basketDiscount(lines);
}

export function basketDiscount(lines) {
  const eligible = new Map();
  for (const item of lines) {
    if (!item.promotionVersion) continue;
    const group = eligible.get(item.productId) ?? {price: item.price, quantity: 0};
    group.quantity += item.units / 1000;
    eligible.set(item.productId, group);
  }
  return [...eligible.values()].reduce((total, group) => total + Math.floor(group.quantity / 3) * group.price, 0);
}

export const demoRegister = '00000000-0000-4000-8000-000000000012';
const demoCashier = '00000000-0000-4000-8000-000000000013';
const demoAdmin = '00000000-0000-4000-8000-000000000014';
const demoRefundLimit = 2_000_000;
const itemKey = item => item.productId + ':' + item.lotId;

export function refundCents(line, units) {
  const sold = BigInt(apiQuantity(line.soldQuantity));
  const cumulative = BigInt(apiQuantity(line.returnedQuantity) + units);
  const total = BigInt(apiMoney(line.soldAmount));
  return Number((total * cumulative * 2n + sold) / (sold * 2n)) - apiMoney(line.returnedAmount);
}

export class RequestError extends Error {
  constructor(status, code, requestId, row) {
    super(code);
    Object.assign(this, {status, code, requestId, row});
  }
}

export const uncertain = e => !e.status || e.status === 408 || e.status >= 500;

export function emptyState() {
  return {version: 1, shift: null, basket: [], pending: null};
}

export const demoGoods = [
  ['Молоко 3,2% · 1 л', '4780011001001', 'piece', 14500, 44, 5],
  ['Хлеб пшеничный', '4780011001002', 'piece', 5500, 58, 3],
  ['Яйца · 10 шт.', '4780011001003', 'piece', 18000, 27, 12],
  ['Вода негазированная · 1,5 л', '4780011001004', 'piece', 4500, 80],
  ['Яблоки красные', '4780011001005', 'kg', 16400, 28, 7],
  ['Рис · 900 г', '4780011001006', 'piece', 17800, 34, 125],
  ['Сахар · 1 кг', '4780011001007', 'piece', 12800, 42],
  ['Кефир · 900 г', '4780011001008', 'piece', 12800, 21, 7],
  ['Масло подсолнечное · 1 л', '4780011001009', 'piece', 21000, 19, 180],
  ['Чай чёрный · 100 г', '4780011001010', 'piece', 22000, 30],
  ['Бананы', '4780011001011', 'kg', 23900, 20, 5],
  ['Печенье овсяное · 300 г', '4780011001012', 'piece', 14000, 22, 20],
];

function uuid(index, prefix) {
  return prefix + '0000000-0000-4000-8000-' + String(index).padStart(12, '0');
}

function expiryIn(days) {
  if (days == null) return null;
  return new Intl.DateTimeFormat('en-CA', {timeZone: 'Asia/Tashkent', dateStyle: 'short'})
    .format(new Date(Date.now() + days * 86400000));
}

function seededGoods() {
  return demoGoods.map(([name, barcode, unit, unitPrice, stock, expiry], i) => ({
    productId: uuid(i + 1, '1'), name, barcode, unit, unitPrice, priceVersion: 1,
    quantityFactor: 1, requiresExpiry: expiry != null, requiresLotVerification: false,
    lots: [
      {lotId: uuid(i + 1, '2'), expiryDate: expiryIn(expiry), quantity: stock},
      ...(i === 0 ? [{lotId: uuid(i + 1, '3'), expiryDate: expiryIn(9), quantity: 24}] : []),
      ...(i === 7 ? [
        {lotId: uuid(i + 1, '3'), expiryDate: expiryIn(-1), quantity: 4},
        {lotId: uuid(i + 1, '4'), expiryDate: expiryIn(0), quantity: 6},
        {lotId: uuid(i + 1, '5'), expiryDate: null, quantity: 3},
      ] : []),
    ]
  }));
}

const demoLedgerKey = 'kasa-demo-ledger-v1';
export function barcodeMatch(product, code) {
  const entries = product.barcodes ?? (product.barcode ? [{code:product.barcode, quantityFactor:product.quantityFactor ?? 1}] : []);
  return entries.find(entry => entry.code === code);
}

export function readDemoLedger(storage) {
  return JSON.parse(storage.getItem(demoLedgerKey) ?? 'null') ?? {goods: seededGoods(), shift: null, sales: []};
}
export function writeDemoLedger(storage, ledger) {
  storage.setItem(demoLedgerKey, JSON.stringify(ledger));
}

export class DemoApi {
  constructor(storage) {
    this.storage = storage;
    this.loseNextSale = false;
  }

  async call(action, value, body) {
    const ledger = readDemoLedger(this.storage);
    ledger.movements ??= [];
    ledger.refunds ??= [];
    ledger.approvals ??= [];
    ledger.shelfTasks ??= [];
    const save = () => writeDemoLedger(this.storage, ledger);
    const findSale = id => ledger.sales.find(row => row.operationId === id);
    const findShift = id => ledger.shift?.shiftId === id ? ledger.shift : null;
    const fail = (status, code) => { throw new RequestError(status, code); };
    const today = expiryIn(0);
    const requireOpen = id => {
      const shift = findShift(id);
      if (!shift || shift.closedAt) return fail(409, 'active_shift_required');
      return shift;
    };
    const refundOptions = (saleId, registerId) => {
      const sale = findSale(saleId);
      if (!sale || registerId !== demoRegister) return fail(404, 'sale_not_found');
      const refunds = ledger.refunds.filter(row => row.saleId === saleId);
      const lines = sale.lines.map(line => {
        const returned = refunds.flatMap(refund => refund.lines).filter(item => itemKey(line) === itemKey(item));
        return {productId: line.productId, lotId: line.lotId, name: line.name, unit: line.unit,
          soldQuantity: line.quantity, soldAmount: line.lineTotal,
          returnedQuantity: returned.reduce((sum, row) => sum + apiQuantity(row.quantity), 0) / 1000,
          returnedAmount: returned.reduce((sum, row) => sum + apiMoney(row.amount), 0) / 100};
      });
      return {sourceRegisterId: demoRegister, saleId, method: sale.method, lines};
    };
    const refundPrice = request => {
      const options = refundOptions(request.saleId, request.sourceRegisterId);
      const lines = request.items.map(item => {
        const line = options.lines.find(line => itemKey(line) === itemKey(item));
        const units = apiQuantity(item.quantity);
        if (!line || !units || units > apiQuantity(line.soldQuantity) - apiQuantity(line.returnedQuantity) ||
            (line.unit === 'piece' && units % 1000)) return fail(409, 'refund_quantity_exceeded');
        return {...item, amount: refundCents(line, units) / 100};
      });
      const total = lines.reduce((sum, line) => sum + apiMoney(line.amount), 0);
      const returned = options.lines.reduce((sum, line) => sum + apiMoney(line.returnedAmount), 0);
      return {lines, total, returned, method: options.method};
    };
    switch (action) {
      case 'search': {
        const query = value.toLowerCase();
        return ledger.goods.filter(p => p.active !== false && (p.name.toLowerCase().includes(query) || p.barcode?.includes(query) || barcodeMatch(p, value)));
      }
      case 'barcode':
      case 'product': {
        const p = ledger.goods.find(p => p.active !== false && (action === 'barcode' ? barcodeMatch(p, value) : p.productId === value));
        if (!p) return fail(404, 'product_not_found');
        const usable = lot => lot.expiryDate ? lot.expiryDate >= today : !p.requiresExpiry;
        const current = p.lots.filter(lot => lot.quantity > 0 && usable(lot));
        const match = action === 'barcode' ? barcodeMatch(p, value) : null;
        return {...p, barcode: match?.code ?? p.barcode, quantityFactor: match?.quantityFactor ?? 1, lots: current,
          requiresLotVerification: p.lots.some(lot => lot.quarantineQuantity > 0 || (lot.quantity > 0 && !usable(lot)))};
      }
      case 'open':
        if (findShift(body.shiftId)) return ledger.shift;
        if (ledger.shift && !ledger.shift.closedAt) return fail(409, 'open_shift_conflict');
        ledger.shift = {shiftId: body.shiftId, openingCash: body.openingCash, expectedCash: body.openingCash,
          salesCount: 0, openedAt: new Date().toISOString(), closedAt: null};
        save();
        return ledger.shift;
      case 'shift':
        return findShift(value) ?? fail(404, 'shift_not_found');
      case 'quote': {
        requireOpen(value);
        const total = body.items.reduce((sum, item) => {
          const product = ledger.goods.find(row => row.productId === item.productId);
          if (!product || product.priceVersion !== item.priceVersion) fail(409, 'price_changed');
          return sum + lineTotal(apiMoney(product.unitPrice), apiQuantity(item.quantity));
        }, 0);
        return {total: total / 100, discountAmount: 0};
      }
      case 'close': {
        const shift = findShift(value) ?? fail(404, 'shift_not_found');
        if (shift.closedAt) {
          if (shift.countedCash !== body.countedCash) return fail(409, 'closed_shift_conflict');
          return shift;
        }
        shift.countedCash = body.countedCash;
        shift.cashDifference = (money(body.countedCash) - apiMoney(shift.expectedCash)) / 100;
        shift.closedAt = new Date().toISOString();
        save();
        return shift;
      }
      case 'sale':
        return findSale(value) ?? fail(404, 'sale_not_found');
      case 'history':
        return ledger.sales.filter(sale => !body || sale.operationId === body)
          .slice(Number(value), Number(value) + 25).map(sale => {
            const options = refundOptions(sale.operationId, demoRegister);
            const refunded = options.lines.reduce((sum, line) => sum + apiMoney(line.returnedAmount), 0);
            const allReturned = options.lines.every(line => line.soldQuantity === line.returnedQuantity);
            return {...sale, receiptId: sale.operationId, registerId: demoRegister, refundedAmount: refunded / 100,
              refundStatus: allReturned ? 'full' : options.lines.some(line => line.returnedQuantity > 0) ? 'partial' : 'none'};
          });
      case 'refundOptions':
        return refundOptions(value, body);
      case 'refund':
        return ledger.refunds.find(row => row.operationId === value) ?? fail(404, 'refund_not_found');
      case 'approveRefund': {
        const request = body.refund;
        const existing = ledger.approvals.find(row => row.operationId === request.operationId);
        if (existing) {
          if (JSON.stringify(existing.original) !== JSON.stringify(body)) return fail(409, 'refund_approval_conflict');
          return existing;
        }
        if (ledger.refunds.some(row => row.operationId === request.operationId)) return fail(409, 'refund_already_completed');
        const {total} = refundPrice(request);
        if (apiMoney(body.amount) !== total || body.registerId !== demoRegister) return fail(409, 'refund_amount_changed');
        requireOpen(request.shiftId);
        const approval = {operationId: request.operationId, registerId: demoRegister, amount: total / 100,
          approvedBy: demoAdmin, original: body};
        ledger.approvals.push(approval);
        save();
        return approval;
      }
      case 'createRefund': {
        const existing = ledger.refunds.find(row => row.operationId === body.operationId);
        if (existing) {
          if (JSON.stringify(existing.original) !== JSON.stringify(body)) return fail(409, 'operation_id_reused');
          return existing;
        }
        const shift = requireOpen(body.shiftId);
        const {lines, total, returned, method} = refundPrice(body);
        const approval = ledger.approvals.find(row => row.operationId === body.operationId);
        if (approval && (apiMoney(approval.amount) !== total || JSON.stringify(approval.original.refund) !== JSON.stringify(body)))
          return fail(409, 'refund_approval_conflict');
        if (!approval && total + returned > demoRefundLimit) return fail(409, 'refund_approval_required');
        if (total > apiMoney(shift.expectedCash)) return fail(409, 'insufficient_cash_balance');
        for (const line of lines) {
          const product = ledger.goods.find(row => row.productId === line.productId);
          const lot = product?.lots.find(row => row.lotId === line.lotId);
          if (!lot) return fail(409, 'stock_restore_conflict');
          if (line.destination === 'quarantine' || (lot.expiryDate ? lot.expiryDate < today : product.requiresExpiry)) {
            line.destination = 'quarantine';
            lot.quarantineQuantity = (apiQuantity(lot.quarantineQuantity ?? 0) + apiQuantity(line.quantity)) / 1000;
          } else {
            lot.quantity = (apiQuantity(lot.quantity) + apiQuantity(line.quantity)) / 1000;
          }
          lot.revision = (lot.revision ?? 1) + 1;
        }
        const result = {operationId: body.operationId, shiftId: body.shiftId, sourceRegisterId: body.sourceRegisterId,
          saleId: body.saleId, method, total: total / 100, reason: body.reason, createdAt: new Date().toISOString(),
          approvedBy: approval?.approvedBy ?? null, lines, receiptMark, original: body};
        shift.refundCount = (shift.refundCount ?? 0) + 1;
        shift.cashRefunds = (apiMoney(shift.cashRefunds ?? 0) + total) / 100;
        shift.expectedCash = (apiMoney(shift.expectedCash) - total) / 100;
        ledger.refunds.unshift(result);
        save();
        return result;
      }
      case 'cashMovements':
        return ledger.movements.filter(row => row.shiftId === value).slice(Number(body), Number(body) + 25);
      case 'createCash': {
        const existing = ledger.movements.find(row => row.operationId === body.operationId);
        if (existing) {
          if (existing.shiftId !== value || existing.direction !== body.direction ||
              existing.amount !== body.amount || existing.reason !== body.reason) return fail(409, 'cash_movement_id_reused');
          return existing;
        }
        const shift = requireOpen(value);
        const delta = apiMoney(body.amount) * (body.direction === 'in' ? 1 : -1);
        const balance = apiMoney(shift.expectedCash) + delta;
        if (balance < 0) return fail(409, 'insufficient_cash_balance');
        const result = {...body, shiftId: value, cashierId: demoCashier, createdAt: new Date().toISOString()};
        shift.expectedCash = balance / 100;
        ledger.movements.unshift(result);
        save();
        return result;
      }
      case 'expiry': {
        const lots = ledger.goods.flatMap(product => product.lots
          .filter(lot => (product.requiresExpiry || lot.expiryDate) && (lot.quantity > 0 || lot.quarantineQuantity > 0))
          .map(lot => ({productId: product.productId, name: product.name, barcode: product.barcode,
            unit: product.unit, requiresExpiry: product.requiresExpiry, lotId: lot.lotId, expiryDate: lot.expiryDate,
            daysRemaining: lot.expiryDate ? (Date.parse(lot.expiryDate) - Date.parse(today)) / 86400000 : null,
            accountedQuantity: (apiQuantity(lot.quantity) + apiQuantity(lot.quarantineQuantity ?? 0)) / 1000})));
        return lots.filter(lot => {
          const days = lot.daysRemaining;
          if (value === 'missing') return days === null && lot.requiresExpiry;
          if (value === 'all') return true;
          if (days === null) return false;
          if (value === 'expired') return days < 0;
          if (value === 'today') return days === 0;
          return days >= 0 && days <= (value === 'within3' ? 3 : 7);
        }).sort((a, b) => (a.expiryDate ?? 'z').localeCompare(b.expiryDate ?? 'z')).slice(Number(body), Number(body) + 50);
      }
      case 'shelf': {
        const existing = ledger.shelfTasks.find(row => row.taskId === body.taskId);
        if (existing) {
          if (itemKey(existing) !== itemKey(body) || existing.quantity !== body.quantity) return fail(409, 'shelf_task_id_reused');
          return existing;
        }
        if (ledger.shelfTasks.some(row => itemKey(row) === itemKey(body) && !row.resolvedAt)) return fail(409, 'shelf_task_already_open');
        const product = ledger.goods.find(row => row.productId === body.productId);
        const lot = product?.lots.find(row => row.lotId === body.lotId);
        if (!lot || body.quantity > lot.quantity) return fail(409, 'shelf_task_quantity_exceeded');
        const result = {...body, createdAt: new Date().toISOString(), createdBy: demoCashier, resolvedAt: null};
        ledger.shelfTasks.unshift(result);
        save();
        return result;
      }
      case 'commit': {
        const existing = findSale(body.operationId);
        if (existing) {
          if (JSON.stringify(existing.original) !== JSON.stringify(body)) return fail(409, 'operation_id_conflict');
          return existing;
        }
        const shift = findShift(body.shiftId);
        if (!shift || shift.closedAt) return fail(409, 'active_shift_required');
        const rows = body.items.map(item => {
          const p = ledger.goods.find(p => p.productId === item.productId);
          const lot = p?.lots.find(lot => lot.lotId === item.lotId);
          const units = quantity(item.quantity);
          if (p?.active === false || p?.unitPrice == null || p.priceVersion !== item.priceVersion) return fail(409, 'price_changed');
          if (!lot || !units || units > apiQuantity(lot.quantity) || (lot.expiryDate ? lot.expiryDate < today : p.requiresExpiry))
            return fail(409, 'stock_unavailable');
          const line = lineTotal(apiMoney(p.unitPrice), units);
          if (!Number.isFinite(line) || line === 0) return fail(400, 'invalid_line_total');
          lot.quantity = (apiQuantity(lot.quantity) - units) / 1000;
          lot.revision = (lot.revision ?? 1) + 1;
          return {...item, name: p.name, unit: p.unit, unitPrice: p.unitPrice, lineTotal: line / 100};
        });
        const total = rows.reduce((sum, row) => sum + apiMoney(row.lineTotal), 0);
        const tendered = money(body.tendered);
        if (tendered == null || tendered < total) return fail(400, 'insufficient_tendered');
        const result = {operationId: body.operationId, shiftId: body.shiftId, total: total / 100,
          method: 'cash', tendered: body.tendered, changeDue: (tendered - total) / 100,
          createdAt: new Date().toISOString(), lines: rows, receiptMark, original: body};
        shift.salesCount += 1;
        shift.expectedCash = (apiMoney(shift.expectedCash) + total) / 100;
        ledger.sales.unshift(result);
        save();
        if (this.loseNextSale) {
          this.loseNextSale = false;
          return fail(0, 'simulated_timeout');
        }
        return result;
      }
      default:
        return fail(400, 'unsupported_action');
    }
  }
}

export function createBridge(webview) {
  if (!webview) return null;
  let nextId = 0;
  const pending = new Map();
  webview.addEventListener('message', ({data}) => {
    const complete = pending.get(data.id);
    if (!complete) return;
    pending.delete(data.id);
    complete(data.result);
  });
  return (action, value, body) => new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new RequestError(0, 'desktop_timeout'));
    }, ['connect', 'approveRefund', 'store.connect', 'store.upload', 'store.download', 'store.management.reportCsv', 'store.management.localCsv', 'device.installPrinterDriver'].includes(action) ? 180000 : 30000);
    pending.set(id, result => {
      clearTimeout(timer);
      if (result.error) {
        const {status, code, requestId, row} = result.error;
        reject(new RequestError(status, code, requestId, row));
      } else resolve(result.data);
    });
    try { webview.postMessage({id, action, data: {value, body}}); }
    catch {
      clearTimeout(timer);
      pending.delete(id);
      reject(new RequestError(0, 'desktop_error'));
    }
  });
}

// A repeated submit always reuses the journaled payload. A read precedes every replay.
export async function findOutcome(call, pending) {
  const {kind, body, shiftId} = pending;
  try {
    if (kind === 'commit') return await call('sale', body.operationId);
    if (kind === 'createRefund') return await call('refund', body.operationId);
    if (kind === 'parkCart') return (await call('cartReview', shiftId, body.cartId))?.cart;
    if (kind === 'createCash') {
      const results = await call('cashMovements', shiftId, '0');
      if (!Array.isArray(results)) throw new RequestError(0, 'invalid_response');
      return results.find(row => row.operationId === body.operationId) ?? null;
    }
    // The cashier API has no read-by-ID for a shelf task. The same POST is idempotent.
    if (kind === 'shelf') return null;
    const shift = await call('shift', shiftId);
    if (kind === 'open') return shift;
    return shift.closedAt ? shift : null;
  } catch (error) {
    const missing = {commit: 'sale_not_found', createRefund: 'refund_not_found', open: 'shift_not_found', parkCart: 'parked_cart_not_found'};
    if (error.status === 404 && error.code === missing[kind]) return null;
    throw error;
  }
}

export function validateOutcome(pending, result) {
  if (pending.kind === 'commit') {
    if (result?.operationId !== pending.body.operationId || result.shiftId !== pending.shiftId ||
        result.method !== 'cash' || !Array.isArray(result.lines) || result.lines.length !== pending.body.items.length ||
        !Number.isFinite(Date.parse(result.createdAt))) return false;
    const lineKey = item => item.productId + ':' + item.lotId;
    const lines = new Map(result.lines.map(line => [lineKey(line), line]));
    const matches = pending.body.items.every(item => {
      const line = lines.get(lineKey(item));
      if (line?.priceVersion !== item.priceVersion || apiQuantity(line.quantity) !== apiQuantity(item.quantity) ||
          (line.promotionVersion ?? null) !== (item.promotionVersion ?? null) ||
          (line.lotPriceVersion ?? null) !== (item.lotPriceVersion ?? null) ||
          (line.priceLabelId ?? null) !== (item.priceLabelId ?? null) || typeof line.name !== 'string') return false;
      const price = apiMoney(line.unitPrice);
      const net = apiMoney(line.lineTotal);
      const discount = apiMoney(line.discountAmount ?? 0);
      return price > 0 && net != null && discount != null &&
        net + discount === lineTotal(price, apiQuantity(item.quantity));
    });
    const total = apiMoney(result.total);
    const tendered = apiMoney(result.tendered);
    const change = apiMoney(result.changeDue);
    return matches && total > 0 && (pending.expectedTotal == null || pending.expectedTotal === total) &&
      total === result.lines.reduce((sum, line) => sum + apiMoney(line.lineTotal), 0) &&
      tendered === apiMoney(pending.body.tendered) && change != null && change === tendered - total;
  }
  if (!result) return false;
  const {kind, body, shiftId} = pending;
  if (kind === 'shelf') return result.taskId === body.taskId && itemKey(result) === itemKey(body) &&
    apiQuantity(result.quantity) === apiQuantity(body.quantity);
  if (result.shiftId !== shiftId) return false;
  if (kind === 'parkCart') {
    if (result.cartId !== body.cartId || !Array.isArray(result.items) || result.items.length !== body.items.length ||
        (['parked', 'expired'].includes(result.status) ? result.version !== 1 :
          !['sold', 'cancelled'].includes(result.status) || result.version !== 2)) return false;
    const items = new Map(result.items.map(item => [itemKey(item), item]));
    return body.items.every(item => {
      const saved = items.get(itemKey(item));
      return saved?.priceVersion === item.priceVersion && apiQuantity(saved.quantity) === apiQuantity(item.quantity) &&
        (saved.promotionVersion ?? null) === (item.promotionVersion ?? null) &&
        (saved.lotPriceVersion ?? null) === (item.lotPriceVersion ?? null) &&
        (saved.priceLabelId ?? null) === (item.priceLabelId ?? null);
    });
  }
  if (kind === 'createCash') return result.operationId === body.operationId && result.direction === body.direction &&
    apiMoney(result.amount) === apiMoney(body.amount) && result.reason === body.reason;
  if (kind === 'createRefund') {
    if (result.operationId !== body.operationId || result.sourceRegisterId !== body.sourceRegisterId ||
        result.saleId !== body.saleId || result.method !== 'cash' || result.reason !== body.reason ||
        !Array.isArray(result.lines) || result.lines.length !== body.items.length || !Number.isFinite(Date.parse(result.createdAt))) return false;
    const lines = new Map(result.lines.map(line => [itemKey(line), line]));
    return body.items.every(item => {
      const line = lines.get(itemKey(item));
      return line && apiQuantity(line.quantity) === apiQuantity(item.quantity) &&
        (line.destination === item.destination || line.destination === 'quarantine') && apiMoney(line.amount) != null;
    }) && apiMoney(result.total) === result.lines.reduce((sum, line) => sum + apiMoney(line.amount), 0);
  }
  return pending.kind === 'open'
    ? !result.closedAt && apiMoney(result.openingCash) === apiMoney(pending.body.openingCash)
    : Boolean(result.closedAt) && apiMoney(result.countedCash) === apiMoney(pending.body.countedCash);
}
