import {RequestError, apiMoney, apiQuantity, barcodeMatch, lineTotal, readDemoLedger, writeDemoLedger} from './model.js';

import {defaultSupplier, managementDemo} from './management-demo.js';
const orderId = '40000000-0000-4000-8000-000000000001';
const adminActions = new Set(['return', 'post', 'costs', 'saveCosts', 'download', 'lots', 'shelfTasks',
  'resolveShelf', 'stockOperation', 'inventoryList', 'inventory', 'inventoryCreate', 'inventoryCount', 'inventoryPost']);
const today = () => new Intl.DateTimeFormat('en-CA', {timeZone: 'Asia/Tashkent', dateStyle: 'short'}).format(new Date());
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const key = row => row.productId + ':' + row.lotId;
const fail = (status, code) => { throw new RequestError(status, code); };

export function warehouseDemo(storage, role, action, value, body) {
  if (action.startsWith('management.')) return managementDemo(storage, role, action.slice(11), value, body);
  if (adminActions.has(action) && role !== 'admin') fail(403, 'store_admin_required');
  const data = readDemoLedger(storage);
  const save = value => { writeDemoLedger(storage, data); return value; };
  const documents = data.receipts ??= [];
  const shelves = data.shelfTasks ??= [];
  const operations = data.stockOperations ??= [];
  const inventories = data.inventories ??= [];
  const proposals = data.productProposals ??= [];
  const getDocument = () => documents.find(row => row.receiptId === value) ?? fail(404, 'receipt_not_found');
  const getInventory = () => inventories.find(row => row.inventoryId === value) ?? fail(404, 'inventory_not_found');
  const productFor = item => data.goods.find(row => row.productId === item.productId);
  const lotFor = item => productFor(item)?.lots.find(row => row.lotId === item.lotId);
  const verified = row => row.files.length > 0 && row.files.every(file => file.scanStatus === 'clean');
  const order = id => {
    const dispatched = (data.procurement ?? []).find(entry => entry.orderId === id && entry.stage === 'sent');
    if (id !== orderId && !dispatched) fail(404, 'purchase_order_not_found');
    const quantities = new Map();
    const initial = dispatched?.lines ?? [{productId:data.goods[1].productId, baseQuantity:12}];
    for (const item of initial) quantities.set(item.productId, (quantities.get(item.productId) ?? 0) + apiQuantity(item.baseQuantity));
    const lines = [...quantities].map(([productId, amount]) => ({productId, ordered:amount/1000, received:0, remaining:amount/1000}));
    for (const receipt of documents.filter(row => row.status === 'posted' && row.purchaseOrderId === id))
      for (const item of receipt.items) {
        const line = lines.find(row => row.productId === item.productId);
        if (line) {
          line.received = (apiQuantity(line.received) + apiQuantity(item.quantity))/1000;
          line.remaining = (apiQuantity(line.ordered) - apiQuantity(line.received))/1000;
        }
      }
    return {orderId:id, supplierRef:dispatched?.supplierCode ?? 'DEMO', status:lines.every(line=>line.remaining===0)?'received':'open', lines};
  };
  const checkOrder = row => {
    if (!row.purchaseOrderId) return;
    const current = order(row.purchaseOrderId);
    if (row.supplierRef !== current.supplierRef) fail(409, 'receipt_order_conflict');
    for (const id of new Set(row.items.map(item => item.productId))) {
      const remaining = current.lines.find(line => line.productId === id)?.remaining ?? 0;
      const sum = row.items.filter(item => item.productId === id).reduce((n, item) => n + apiQuantity(item.quantity), 0);
      if (sum > apiQuantity(remaining)) fail(409, 'receipt_order_conflict');
    }
  };
  const lots = () => data.goods.flatMap(product => product.lots.map(lot => ({
    productId: product.productId, lotId: lot.lotId, name: product.name, barcode: product.barcode, unit: product.unit,
    expiryDate: lot.expiryDate, stockQuantity: lot.quantity, quarantineQuantity: lot.quarantineQuantity ?? 0,
    availableQuantity: (lot.expiryDate ? lot.expiryDate >= today() : !product.requiresExpiry) ? lot.quantity : 0,
    revision: lot.revision ?? 1,
  }))).sort((a, b) => a.name.localeCompare(b.name, 'ru') || a.lotId.localeCompare(b.lotId));
  // Mirror the response boundary: receiver documents carry no pricing or administrative state.
  const view = ({files, costs, creation, lastAction, lastBody, ...receipt}) => receipt;
  switch (action) {
    case 'suppliers': return (data.supplierRegistry ?? [defaultSupplier]).filter(row=>row.active).map(({code,name})=>({code,name}));
    case 'productInfo': return data.goods.map(({productId, name, unit}) => ({productId, name, unit}));
    case 'orders': return [orderId,...(data.procurement ?? []).filter(row=>row.stage==='sent').map(row=>row.orderId)]
      .map(order).filter(row=>row.status==='open').slice(Number(value),Number(value)+25);
    case 'order': return order(value);
    case 'barcode': {
      const p = data.goods.find(row => row.active !== false && barcodeMatch(row,value));
      if (!p) return fail(404, 'product_not_found');
      const {productId, name, unit, requiresExpiry} = p;
      return {productId, name, barcode:value, unit, requiresExpiry, quantityFactor:barcodeMatch(p,value).quantityFactor};
    }
    case 'receipts':
      return documents.filter(row => !body || row.status === body).slice(Number(value), Number(value) + 25).map(view);
    case 'receipt': return view(getDocument());
    case 'create': {
      const existing = documents.find(row => row.receiptId === body.receiptId);
      if (existing) return same(existing.creation, body) ? view(existing) : fail(409, 'receipt_id_reused');
      if (documents.some(row => row.supplierRef === body.supplierRef && row.invoiceNumber === body.invoiceNumber && row.invoiceDate === body.invoiceDate))
        return fail(409, 'duplicate_invoice');
      checkOrder(body);
      const row = {...body, creation: body, status: 'draft', version: 1, files: [], costs: null};
      documents.unshift(row);
      return save(view(row));
    }
    case 'edit': case 'submit': case 'return': case 'post': case 'cancel': {
      const row = getDocument();
      if (row.version === body.expectedVersion + 1 && row.lastAction === action && same(row.lastBody, body)) return view(row);
      if (row.version !== body.expectedVersion) fail(409, 'receipt_version_conflict');
      const needed = ['post', 'return'].includes(action) ? ['submitted'] :
        action === 'cancel' && role === 'admin' ? ['draft', 'submitted'] : ['draft'];
      if (!needed.includes(row.status)) fail(409, 'receipt_version_conflict');
      if ((action === 'submit' || action === 'post') && !verified(row)) fail(409, 'invoice_file_required');
      if (action === 'edit') { checkOrder({...row, items: body.items}); row.items = body.items; }
      if (action === 'post') {
        if (row.costs?.receiptVersion !== row.version) fail(409, 'receipt_costs_required');
        checkOrder(row);
        for (const item of row.items) {
          const product = productFor(item);
          if (!product || (item.expiryDate ? item.expiryDate < today() : product.requiresExpiry)) fail(409, 'receipt_expiry_invalid');
          if (lotFor(item)) fail(409, 'receipt_lot_conflict');
          product.lots.push({lotId: item.lotId, expiryDate: item.expiryDate, quantity: item.quantity});
        }
        row.postedAt = new Date().toISOString();
      }
      row.status = {edit: 'draft', return: 'draft', submit: 'submitted', post: 'posted', cancel: 'cancelled'}[action];
      row.reviewNote = action === 'return' ? body.reason : action === 'edit' ? row.reviewNote : null;
      if (action === 'cancel') row.cancellation = {reason: body.reason, cancelledAt: new Date().toISOString()};
      row.version += 1;
      row.lastAction = action;
      row.lastBody = body;
      return save(view(row));
    }
    case 'costs': {
      const row = getDocument();
      if (!row.costs) fail(404, 'receipt_costs_not_found');
      return {...row.costs, status: row.status === 'posted' ? 'posted' : row.costs.receiptVersion === row.version ? 'reviewed' : 'stale'};
    }
    case 'saveCosts': {
      const row = getDocument();
      if (row.costs && row.lastAction === action && same(row.lastBody, body)) return row.costs;
      if (row.status !== 'submitted' || row.version !== body.expectedVersion) fail(409, 'receipt_version_conflict');
      const lines = row.items.map(item => {
        const cost = body.items.find(line => key(line) === key(item));
        if (!cost) fail(400, 'receipt_costs_require_all_items');
        const gross = lineTotal(apiMoney(cost.unitPrice), apiQuantity(item.quantity));
        const total = gross - apiMoney(cost.discount) + apiMoney(cost.includedTax) + apiMoney(cost.allocatedCharges);
        if (!Number.isFinite(total) || apiMoney(cost.discount) > gross) fail(400, 'invalid_receipt_cost_amount');
        return {...cost, total: total / 100, quantity: item.quantity};
      });
      row.costs = {receiptId: value, receiptVersion: ++row.version, status: 'reviewed', lines,
        total: lines.reduce((sum, line) => sum + apiMoney(line.total), 0) / 100};
      row.lastAction = action;
      row.lastBody = body;
      return save(row.costs);
    }
    case 'files': return getDocument().files;
    case 'upload': {
      const row = getDocument();
      if (row.status !== 'draft') fail(409, 'receipt_not_editable');
      const existing = row.files.find(file => file.fileId === body);
      if (existing) return existing;
      if (row.files.length >= 3) fail(409, 'invoice_file_limit');
      const file = {fileId: body, fileName: 'Учебный образец.pdf', contentType: 'application/pdf', sizeBytes: 0, scanStatus: 'clean'};
      row.files.push(file);
      return save(file);
    }
    case 'delete': {
      const row = getDocument();
      if (row.status !== 'draft') fail(409, 'receipt_not_editable');
      row.files = row.files.filter(file => file.fileId !== body);
      return save(true);
    }
    case 'scan': return getDocument().files.find(file => file.fileId === body) ?? fail(404, 'invoice_file_not_found');
    case 'download': return false;
    case 'propose': {
      const existing = proposals.find(row => row.proposalId === body.proposalId);
      if (existing) return existing;
      const proposal = {...body, status: 'pending'};
      proposals.unshift(proposal);
      return save(proposal);
    }
    case 'lots': return lots().filter(row => !body || row.productId === body).slice(Number(value), Number(value) + 50);
    case 'shelfTasks': return shelves.filter(row => !row.resolvedAt).slice(Number(value), Number(value) + 25);
    case 'resolveShelf': {
      const task = shelves.find(row => row.taskId === value);
      if (!task) fail(404, 'shelf_task_not_found');
      if (task.resolvedAt && task.resolution !== body.resolution) fail(409, 'shelf_task_already_resolved');
      task.resolution = body.resolution;
      task.resolvedAt ??= new Date().toISOString();
      return save(task);
    }
    case 'stockOperation': {
      const existing = operations.find(row => row.operationId === body.operationId);
      if (existing) return same(existing.request, body) ? existing : fail(409, 'stock_operation_id_reused');
      const lot = lotFor(body);
      const amount = apiQuantity(body.quantity);
      const quarantineBefore = apiQuantity(lot?.quarantineQuantity ?? 0);
      const stockBefore = apiQuantity(lot?.quantity);
      if (!lot || !amount || (body.kind === 'writeOffQuarantine' ? quarantineBefore : stockBefore) < amount)
        fail(409, 'stock_quantity_conflict');
      lot.quantity = (stockBefore - (body.kind === 'writeOffQuarantine' ? 0 : amount)) / 1000;
      lot.quarantineQuantity = (quarantineBefore + (body.kind === 'quarantine' ? amount : body.kind === 'writeOffQuarantine' ? -amount : 0)) / 1000;
      lot.revision = (lot.revision ?? 1) + 1;
      const result = {operationId: body.operationId, kind: body.kind, reason: body.reason, createdAt: new Date().toISOString(), request: body};
      operations.unshift(result);
      return save(result);
    }
    case 'inventoryList': return inventories.slice(Number(value), Number(value) + 25);
    case 'inventory': return getInventory();
    case 'inventoryCreate': {
      const existing = inventories.find(row => row.inventoryId === body.inventoryId);
      if (existing) return existing;
      const allLots = lots();
      const lines = body.lots.map(item => {
        const lot = allLots.find(row => key(row) === key(item));
        if (!lot) fail(404, 'stock_lot_not_found');
        return {productId: item.productId, lotId: item.lotId, name: lot.name, unit: lot.unit, lotRevision: lot.revision,
          expectedStock: lot.stockQuantity, expectedQuarantine: lot.quarantineQuantity, countedStock: null, countedQuarantine: null};
      });
      const doc = {inventoryId: body.inventoryId, reason: body.reason, version: 1, status: 'draft', lines};
      inventories.unshift(doc);
      return save(doc);
    }
    case 'inventoryCount': case 'inventoryPost': {
      const doc = getInventory();
      if (doc.version === body.expectedVersion + 1 && doc.lastAction === action && same(doc.lastBody, body)) return doc;
      if (doc.version !== body.expectedVersion || (action === 'inventoryPost' ? doc.status !== 'counted' : doc.status === 'posted'))
        fail(409, 'inventory_version_conflict');
      for (const line of doc.lines) {
        const lot = lotFor(line);
        if ((lot.revision ?? 1) !== line.lotRevision) fail(409, 'inventory_lot_changed');
        if (action === 'inventoryCount') {
          const row = body.items.find(item => key(item) === key(line));
          if (!row) fail(400, 'invalid_inventory_count');
          line.countedStock = row.stockQuantity;
          line.countedQuarantine = row.quarantineQuantity;
          line.countedRevision = row.lotRevision;
        } else {
          if (line.countedStock == null || line.countedQuarantine == null) fail(409, 'inventory_not_counted');
          lot.quantity = line.countedStock;
          lot.quarantineQuantity = line.countedQuarantine;
          lot.revision = (lot.revision ?? 1) + 1;
        }
      }
      doc.lastAction = action;
      doc.lastBody = body;
      doc.status = action === 'inventoryPost' ? 'posted' : 'counted';
      doc.version++;
      return save(doc);
    }
    default: return fail(400, 'unsupported_action');
  }
}
