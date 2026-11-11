import {RequestError, money, apiMoney, barcodeMatch, readDemoLedger, writeDemoLedger} from './model.js';

export const storeDay = (date = new Date()) => new Intl.DateTimeFormat('en-CA',
  {timeZone:'Asia/Tashkent', dateStyle:'short'}).format(date);
export const defaultSupplier = {code:'DEMO', name:'Учебный поставщик', taxId:'', phone:'', email:'',
  paymentTerms:'', leadTimeDays:1, active:true, version:1};
const fail = (status, code) => { throw new RequestError(status, code); };
const same = (a,b) => JSON.stringify(a) === JSON.stringify(b);
const now = () => new Date().toISOString();
const datePlus = (day, count) => new Date(Date.parse(day) + count * 86400000).toISOString().slice(0,10);
const moneySum = (rows, get) => rows.reduce((total, row) => total + (apiMoney(get(row)) ?? 0), 0) / 100;
const csvHeader = 'productId,expectedVersion,name,unit,requiresExpiry,sku,category,brand,supplierRef,taxProfile,barcodes,unitPrice,expectedPriceVersion';

function readCsv(csv) {
  const rows = [];
  let fields = [];
  let value = '';
  let quoted = false;
  let closed = false;
  for (let i = 0; i <= csv.length; i++) {
    const char = csv[i] ?? '\n';
    if (quoted) {
      if (char === '"' && csv[i+1] === '"') { value += '"'; i++; }
      else if (char === '"') { quoted = false; closed = true; }
      else value += char;
    } else if (char === ',' || char === '\n' || char === '\r') {
      fields.push(value);
      value = ''; closed = false;
      if (char !== ',') {
        if (fields.some(Boolean)) rows.push(fields);
        fields = [];
        if (char === '\r' && csv[i+1] === '\n') i++;
      }
    } else if (closed) fail(400,'invalid_catalog_csv_row');
    else if (char === '"') {
      if (value) fail(400,'invalid_catalog_csv_row');
      quoted = true;
    } else value += char;
  }
  if (quoted || !rows.length || rows.shift().join(',').replace(/^\uFEFF/,'') !== csvHeader)
    fail(400,'invalid_catalog_csv_header');
  if (!rows.length || rows.length > 500 || rows.some(line => line.length !== 13)) fail(400,'invalid_catalog_csv_row');
  return rows;
}

export function managementDemo(storage, role, action, value, body) {
  if (role !== 'admin') fail(403,'store_admin_required');
  const data = readDemoLedger(storage);
  const suppliers = data.supplierRegistry ??= [{...defaultSupplier}];
  const drafts = data.procurement ??= [];
  const quotes = data.supplierQuotes ??= [];
  const proposals = data.productProposals ??= [];
  const audit = data.managementAudit ??= [];
  const page = (rows, offset = value, size = 25) => rows.slice(Number(offset), Number(offset) + size);
  const getProduct = id => data.goods.find(product => product.productId === id);
  const supplier = code => suppliers.find(row => row.code === code) ?? fail(404,'supplier_not_found');
  const draft = () => drafts.find(row => row.orderId === value) ?? fail(404,'purchase_order_not_found');
  const card = row => {
    const {lots, priceHistory, lastCard, lastPrice, ...fields} = row;
    return {...fields, version: row.version ?? 1, active: row.active ?? true, sku:row.sku ?? '',
      barcodes: row.barcodes ?? (row.barcode ? [{code:row.barcode, quantityFactor:row.quantityFactor ?? 1}] : [])};
  };
  const persist = (result, verb, id) => {
    if (verb) audit.unshift({id:(audit[0]?.id ?? 0)+1, occurredAt:now(), action:verb, staffId:'Учебный администратор', objectId:id});
    writeDemoLedger(storage, data);
    return result;
  };
  function saveCard(id, input) {
    const old = getProduct(id);
    if ((old?.version ?? (old ? 1 : 0)) === input.expectedVersion + 1 && same(old.lastCard,input)) return old;
    if ((old?.version ?? (old ? 1 : 0)) !== input.expectedVersion) fail(409,'product_version_conflict');
    if (!input.name || !['piece','kg'].includes(input.unit) || (old && old.unit !== input.unit)) fail(400,'invalid_product');
    if (input.barcodes.some((entry,index) => !/^\d{1,48}$/.test(entry.code) || !(entry.quantityFactor > 0) ||
      (input.unit === 'piece' && !Number.isInteger(entry.quantityFactor)) ||
      input.barcodes.findIndex(item => item.code === entry.code) !== index ||
      data.goods.some(other => other.productId !== id && barcodeMatch(other,entry.code))))
      fail(409,'product_barcode_in_use');
    if (input.sku && data.goods.some(other => other.productId !== id && other.sku?.toLowerCase() === input.sku.toLowerCase()))
      fail(409,'product_sku_in_use');
    const {expectedVersion, ...fields} = input;
    const result = old ?? {productId:id, lots:[], active:true, unitPrice:null, priceVersion:1,
      priceHistory:[{version:1,unitPrice:null,active:true,reason:'product_created',recordedAt:now()}]};
    Object.assign(result, fields, {version:expectedVersion+1, barcode:input.barcodes[0]?.code ?? '',
      quantityFactor:input.barcodes[0]?.quantityFactor ?? 1, lastCard:input});
    if (!old) data.goods.push(result);
    return result;
  }
  function savePrice(id, input) {
    const product = getProduct(id) ?? fail(404,'product_not_found');
    const history = product.priceHistory ??= [];
    if (product.priceVersion === input.expectedVersion+1 && same(product.lastPrice,input)) return history[0];
    if (product.priceVersion !== input.expectedVersion) fail(409,'price_version_conflict');
    if (!input.reason || (input.unitPrice !== null && !(money(input.unitPrice) > 0))) fail(400,'invalid_product_price');
    Object.assign(product,{unitPrice:input.unitPrice, active:input.active, priceVersion:input.expectedVersion+1,lastPrice:input});
    const result = {version:product.priceVersion,unitPrice:input.unitPrice,active:input.active,reason:input.reason,recordedAt:now()};
    history.unshift(result);
    return result;
  }
  const effective = (code, day = storeDay()) => {
    const filtered = quotes.filter(quote => quote.code === code && quote.validFrom <= day && (!quote.validThrough || quote.validThrough >= day));
    return [...new Set(filtered.map(row => row.supplierSku))].sort().map(sku => filtered.filter(row => row.supplierSku === sku)
      .sort((a,b) => b.validFrom.localeCompare(a.validFrom) || b.version-a.version)[0]);
  };
  switch (action) {
    case 'catalog': {
      const q = (body ?? '').toLowerCase();
      return page(data.goods.filter(row => [row.name,row.sku,row.barcode].some(text => text?.toLowerCase().includes(q))).map(card));
    }
    case 'product': return card(getProduct(value) ?? fail(404,'product_not_found'));
    case 'saveProduct': return persist(card(saveCard(value,body)),'product.saved',value);
    case 'priceHistory': return getProduct(value)?.priceHistory ?? [];
    case 'savePrice': return persist(savePrice(value,body),'product.price_updated',value);
    case 'catalogImport': {
      const entries = readCsv(body.csv);
      const ids = new Set();
      const products = entries.map(([id, version, name, unit, expiry, sku, category, brand, supplierRef, taxProfile, barcodes, price, priceVersion]) => {
        if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(id) || ids.has(id) ||
            !/^\d+$/.test(version) || !/^(true|false)$/i.test(expiry)) fail(400,'invalid_catalog_csv_row');
        ids.add(id);
        const codes = barcodes ? barcodes.split(';').map(entry => {
          const [code, factor='1'] = entry.split('*');
          return {code,quantityFactor:Number(factor)};
        }) : [];
        const product = saveCard(id,{expectedVersion:Number(version),name,unit,requiresExpiry:expiry.toLowerCase()==='true',
          sku,category,brand,supplierRef,taxProfile,barcodes:codes});
        if (price || priceVersion) {
          if (!/^\d+$/.test(priceVersion) || money(price) === null) fail(400,'invalid_product_price');
          savePrice(id,{expectedVersion:Number(priceVersion),unitPrice:Number(price),active:true,reason:'catalog_import'});
        }
        return card(product);
      });
      const result = {preview:body.preview,products};
      return body.preview ? result : persist(result,'catalog.imported','—');
    }
    case 'proposals': return page(proposals.filter(row => row.status==='pending'));
    case 'approveProposal': case 'rejectProposal': {
      const row = proposals.find(item => item.proposalId===value) ?? fail(404,'product_proposal_not_found');
      const status = action === 'approveProposal' ? 'approved' : 'rejected';
      if (row.status === status && row.reviewReason === body.reason) return row;
      if (row.status !== 'pending') fail(409,'product_proposal_already_reviewed');
      if (status === 'approved') {
        saveCard(value,{...row.product,expectedVersion:0});
        row.publishedProductId = value;
      }
      Object.assign(row,{status,reviewReason:body.reason,reviewedAt:now()});
      return persist(row,'product.proposal_'+status,value);
    }
    case 'suppliers': return page(suppliers.map(({code,name}) => ({code,name})));
    case 'supplier': return supplier(value);
    case 'saveSupplier': {
      const i = suppliers.findIndex(row => row.code === value);
      const old = suppliers[i];
      const {expectedVersion,...fields} = body;
      const result = {...fields,code:value,version:expectedVersion+1};
      if (old && same(old,result)) return old;
      if ((old?.version ?? 0) !== expectedVersion) fail(409,'supplier_version_conflict');
      if (i < 0) suppliers.push(result); else suppliers[i] = result;
      return persist(result,'supplier.saved',value);
    }
    case 'quotes': return page(effective(value),body);
    case 'quoteHistory': return quotes.filter(row => row.code+'|'+row.supplierSku===value).sort((a,b)=>b.version-a.version);
    case 'saveQuote': {
      const [code,sku] = value.split('|');
      if (!supplier(code).active) fail(409,'supplier_inactive');
      const old = quotes.filter(row => row.code===code && row.supplierSku===sku).sort((a,b)=>b.version-a.version)[0];
      if (old?.version === body.expectedVersion+1 && same(old.request,body)) return old;
      if ((old?.version ?? 0) !== body.expectedVersion || (old && old.productId!==body.productId))
        fail(409,'supplier_price_version_conflict');
      if (!getProduct(body.productId)) fail(404,'product_not_found');
      const {expectedVersion,...fields} = body;
      const result = {...fields,code,supplierSku:sku,version:expectedVersion+1,request:body};
      quotes.unshift(result);
      return persist(result,'supplier.price_updated',sku);
    }
    case 'drafts': return page(drafts);
    case 'draft': return draft();
    case 'saveDraft': {
      if (!supplier(body.supplierCode).active) fail(409,'supplier_inactive');
      const index = drafts.findIndex(row=>row.orderId===value);
      const old = drafts[index];
      if (old?.revision===body.expectedRevision+1 && same(old.request,body)) return old;
      if (old?.replenishment&&!body.replenishment) fail(409,'purchase_replenishment_origin_required');
      if ((old?.revision ?? 0)!==body.expectedRevision || old?.stage==='sent' ||
          (old && old.supplierCode!==body.supplierCode)) fail(409,'purchase_order_revision_conflict');
      const lines = body.items.map(item=>{
        const quote = effective(body.supplierCode).find(row=>row.supplierSku===item.supplierSku);
        if (!quote || quote.version!==item.priceVersion) fail(409,'supplier_price_changed');
        return {...item,productId:quote.productId,packageUnit:quote.packageUnit,unitsPerPackage:quote.unitsPerPackage,
          baseQuantity:item.packages*quote.unitsPerPackage,packagePrice:quote.packagePrice,
          total:apiMoney(quote.packagePrice)*item.packages/100};
      });
      const result = {orderId:value,stage:'draft',supplierCode:body.supplierCode,expectedDeliveryDate:body.expectedDeliveryDate,
        revision:body.expectedRevision+1,lines,replenishment:body.replenishment,total:moneySum(lines,row=>row.total),request:body};
      if (index<0) drafts.unshift(result); else drafts[index]=result;
      return persist(result,'purchase_order.draft_saved',value);
    }
    case 'approveDraft': case 'markSent': {
      const order = draft();
      const next = action==='approveDraft' ? 'approved':'sent';
      if (order.revision!==body.expectedRevision) fail(409,'purchase_order_revision_conflict');
      if (order.stage===next && (next==='approved' || order.dispatchReference===body.reference)) return order;
      if (order.stage!==(next==='approved'?'draft':'approved')) fail(409,'purchase_order_stage_conflict');
      order.stage=next;
      if (next==='sent') order.dispatchReference=body.reference;
      return persist(order,'purchase_order.'+next,value);
    }
    case 'recommend': {
      const [code, sku] = value.split('|');
      const day = storeDay();
      const leadTime = body.leadTimeDays ?? supplier(code).leadTimeDays;
      const weekdays=body.orderWeekdays ?? [1,2,3,4,5,6,7];
      const localClock=new Intl.DateTimeFormat('en-GB',{timeZone:'Asia/Tashkent',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).format(new Date());
      let orderDate=body.orderCutoffTime && localClock>=body.orderCutoffTime.slice(0,5) ? datePlus(day,1) : day;
      while (!weekdays.includes(new Date(orderDate+'T00:00:00Z').getUTCDay()||7)) orderDate=datePlus(orderDate,1);
      const arrivalDate = datePlus(orderDate,leadTime);
      const quote = effective(code,orderDate).find(row=>row.supplierSku===sku);
      if (!quote) fail(404,'supplier_price_not_found');
      const product = getProduct(quote.productId);
      const sellableStock = product.lots.filter(lot=>lot.expiryDate ? lot.expiryDate>=day : !product.requiresExpiry)
        .reduce((sum,lot)=>sum+lot.quantity,0);
      const samples = data.sales.filter(sale=>storeDay(new Date(sale.createdAt))<day &&
        storeDay(new Date(sale.createdAt))>=datePlus(day,-body.historyDays));
      const usableDays = new Set(samples.map(sale=>storeDay(new Date(sale.createdAt)))).size;
      const units = samples.flatMap(sale=>sale.lines).filter(line=>line.productId===quote.productId)
        .reduce((sum,line)=>sum+line.quantity,0);
      const unitsPerDay = body.manualDailyDemand ?? (usableDays>=body.minimumHistoryDays ? units/usableDays : null);
      const deliveries=drafts.filter(order=>order.stage==='sent').map(order=>{
        const planned=order.lines.filter(line=>line.productId===product.productId).reduce((sum,line)=>sum+line.baseQuantity,0);
        const accepted=(data.receipts??[]).filter(row=>row.status==='posted'&&row.purchaseOrderId===order.orderId)
          .flatMap(row=>row.items).filter(line=>line.productId===product.productId).reduce((sum,line)=>sum+line.quantity,0);
        return {orderId:order.orderId,expectedDate:order.expectedDeliveryDate,remainingQuantity:Math.max(0,planned-accepted)};
      }).filter(order=>order.remainingQuantity>0);
      const stockForArrival=product.lots.filter(lot=>lot.expiryDate ? lot.expiryDate>=arrivalDate : !product.requiresExpiry)
        .reduce((sum,lot)=>sum+lot.quantity,0);
      const daysToArrival=(Date.parse(arrivalDate)-Date.parse(day))/86400000;
      const incoming=deliveries.filter(row=>row.expectedDate<=arrivalDate).reduce((sum,row)=>sum+row.remainingQuantity,0);
      const stockAtArrival = unitsPerDay===null ? null : Math.max(0,stockForArrival+incoming-unitsPerDay*daysToArrival);
      const targetQuantity = unitsPerDay===null ? null : unitsPerDay*body.coverageDays+body.safetyStock;
      const suggestedPackages = targetQuantity===null ? null : targetQuantity<=stockAtArrival ? 0 : Math.max(body.minimumPackages,Math.ceil((targetQuantity-stockAtArrival)/quote.unitsPerPackage));
      return {calculationVersion:'demo',calculatedAt:now(),storeDate:day,orderDate,arrivalDate,productName:product.name,sellableStock,
        demand:{source:body.manualDailyDemand==null?'history':'manual',insufficientHistory:usableDays<body.minimumHistoryDays,unitsPerDay,usableDays},
        projection:unitsPerDay===null?null:{stockAtArrival,targetQuantity,risk:stockAtArrival>0?'normal':'shortage'},
        deliveries,quote,suggestedPackages,suggestedQuantity:suggestedPackages===null?null:suggestedPackages*quote.unitsPerPackage,
        draft:suggestedPackages>0&&orderDate===day?{supplierCode:code,expectedDeliveryDate:arrivalDate,expectedRevision:0,
          items:[{supplierSku:sku,priceVersion:quote.version,packages:suggestedPackages}],replenishment:{settings:body}}:null};
    }
    case 'report': return demoReport(data,value,body);
    default: return fail(400,'unsupported_action');
  }
}

function demoReport(data, type, period) {
  const within = timestamp => timestamp && storeDay(new Date(timestamp))>=period.from && storeDay(new Date(timestamp))<=period.to;
  const sales = data.sales.filter(row=>within(row.createdAt));
  const refunds = (data.refunds ?? []).filter(row=>within(row.createdAt));
  const movements = (data.movements ?? []).filter(row=>within(row.createdAt));
  const meta = {...period,timeZone:'Asia/Tashkent',generatedAt:now()};
  const totals = (s,r,m) => {
    const cashSales=moneySum(s,row=>row.total), cashRefunds=moneySum(r,row=>row.total);
    return {saleCount:s.length,refundCount:r.length,sales:cashSales,cashSales,cashRefunds,refunds:cashRefunds,
      netSales:(apiMoney(cashSales)-apiMoney(cashRefunds))/100,manualCashlessSales:0,manualCashlessRefunds:0,
      cashIn:moneySum(m.filter(row=>row.direction==='in'),row=>row.amount),
      cashOut:moneySum(m.filter(row=>row.direction==='out'),row=>row.amount)};
  };
  const profit = values => ({sales:values.sales,refunds:values.refunds,netRevenue:values.netSales,
    costOfGoodsSold:values.saleCount||values.refundCount?null:0,grossProfit:values.saleCount||values.refundCount?null:0,
    grossMarginPercent:null,unvaluedSaleLineCount:sales.flatMap(row=>row.lines).length});
  if (type==='sales'||type==='profit') {
    const days = [...new Set([...sales,...refunds,...movements].map(row=>storeDay(new Date(row.createdAt))))].sort();
    const forDate = (rows,date) => rows.filter(row=>storeDay(new Date(row.createdAt))===date);
    const convert = type==='sales' ? values=>values : profit;
    return {...meta,total:convert(totals(sales,refunds,movements)),
      days:days.map(date=>({date,amounts:convert(totals(forDate(sales,date),forDate(refunds,date),forDate(movements,date)))}))};
  }
  const products = new Map(data.goods.map(product=>[product.productId,product.name]));
  const rows = type==='audit' ? (data.managementAudit ?? []).filter(row=>within(row.occurredAt)) : [
    ...(data.receipts ?? []).filter(row=>row.status==='posted'&&within(row.postedAt)).flatMap(row=>row.items.map(line=>({
      ...line,kind:'receipt',occurredAt:row.postedAt,stockDelta:line.quantity,quarantineDelta:0}))),
    ...sales.flatMap(row=>row.lines.map(line=>({...line,kind:'sale',occurredAt:row.createdAt,stockDelta:-line.quantity,quarantineDelta:0}))),
    ...refunds.flatMap(row=>row.lines.map(line=>({...line,kind:'refund',occurredAt:row.createdAt,
      stockDelta:line.destination==='stock'?line.quantity:0,quarantineDelta:line.destination==='quarantine'?line.quantity:0}))),
    ...(data.stockOperations ?? []).filter(row=>within(row.createdAt)).map(row=>({...row.request,occurredAt:row.createdAt,
      stockDelta:row.kind==='writeOffQuarantine'?0:-row.request.quantity,
      quarantineDelta:row.kind==='quarantine'?row.request.quantity:row.kind==='writeOffQuarantine'?-row.request.quantity:0})),
  ].filter(row=>type!=='stock-losses'||row.kind.startsWith('writeOff')).map(row=>({...row,productName:products.get(row.productId)}))
    .sort((a,b)=>b.occurredAt.localeCompare(a.occurredAt));
  const offset = Number(period.after ?? 0);
  return {...meta,items:rows.slice(offset,offset+50),nextCursor:rows.length>offset+50?String(offset+50):null};
}
