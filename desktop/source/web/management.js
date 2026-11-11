import {t} from './i18n.js';
import {formatMoney, money, quantity} from './model.js';
import {storeDay} from './management-demo.js';

const tabs = {catalog:t('Каталог товаров'),proposals:t('Предложения приёмщиков'),suppliers:t('Поставщики'),
  drafts:t('Заказы поставщикам'),reports:t('Отчёты и аудит')};
const reports = [['sales',t('Продажи и оплаты')],['profit',t('Выручка и маржа')],['stock-movements',t('Движения по складу')],
  ['stock-losses',t('Потери и списания')],['audit',t('Журнал действий')]];
const stage = {draft:t('Черновик'),approved:t('Утверждён'),sent:t('Отправлен')};
const amount = value => value == null ? '—' : formatMoney(Math.round(Number(value)*100));
const labelId = value => value ? value.slice(0,8) + '…' + value.slice(-4) : '—';
const date = value => value ? String(value).slice(0,10).split('-').reverse().join('.') : '—';
const humanTime = (value,timeZone='Asia/Tashkent') => value ? new Date(value).toLocaleString('ru-RU',{timeZone}) : '—';
const number = value => value == null ? '—' : new Intl.NumberFormat('ru-RU',{maximumFractionDigits:3}).format(value);
const codePattern = /^[A-Z0-9][A-Z0-9_-]{0,31}$/;
const guidPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const csvHeader = 'productId,expectedVersion,name,unit,requiresExpiry,sku,category,brand,supplierRef,taxProfile,barcodes,unitPrice,expectedPriceVersion';

export function mountManagement({bridge,$,element,notice,emptyRow,request,write,run,ask,say,admin,online}) {
  let tab = 'catalog';
  let view = null;
  let offset = 0;
  let count = 0;
  let query = '';
  let csv = null;
  let period = null;
  let reportKind = 'sales';
  let reportData = null;
  let cursors = [null];
  let cursorIndex = 0;
  let locked = false;

  const read = (action,value,body) => request('management.'+action,value,body);
  const mutate = (action,value,body,field,id) => write('management.'+action,value,body,field,id);
  const field = (key,label,value='',extra={}) => ({key,label,value:String(value ?? ''),...extra});
  const optional = (key,label,value='',maxLength=160) => field(key,label,value,{optional:true,maxLength});
  const requireDate = value => {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value) || !Number.isFinite(Date.parse(value)) ||
        new Date(value).toISOString().slice(0,10)!==value) throw new Error(t('Укажите корректную дату.'));
    return value;
  };
  const requireCode = value => {
    const code=value.toUpperCase();
    if (!codePattern.test(code)) throw new Error(t('Код должен содержать до 32 латинских букв, цифр, дефисов или подчёркиваний.'));
    return code;
  };
  const requireMoney = (value, allowZero=false) => {
    const cents=money(value);
    if (cents===null || (!allowZero && cents===0)) throw new Error(t('Укажите корректную сумму с точностью до двух знаков после запятой.'));
    return cents/100;
  };
  const bounded = (value,min,max) => {
    const parsed=Number(value);
    if (!/^\d+$/.test(value) || !Number.isSafeInteger(parsed) || parsed<min || parsed>max)
      throw new Error(t('Введите целое число от ') + min + t(' до ') + max + '.');
    return parsed;
  };
  const button = (text, handler, style='outline', writeAction=false) => {
    const node=element('button',text,'button '+style);
    node.type='button';
    node.disabled=writeAction && locked;
    if (writeAction) node.dataset.mMutation='';
    node.addEventListener('click', handler);
    return node;
  };
  const group = (...items) => {
    const node=element('div',null,'actions');
    node.append(...items);
    return node;
  };
  const open = next => run(async()=>{ view=next; offset=0; say(''); await refresh(); });
  const back = () => open(null);
  const action = (label, fn, style='outline', writeAction=false) => button(label,()=>run(fn),style,writeAction);
  const table = (headers, rows) => {
    $('m-head').replaceChildren(...headers.map(header=>element('th',header)));
    const entries=rows.map(values=>{
      const tr=element('tr');
      for (const value of values) {
        const td=element('td');
        if (value instanceof HTMLElement) td.append(value);
        else td.textContent=value;
        tr.append(td);
      }
      return tr;
    });
    $('m-body').replaceChildren(...(entries.length ? entries:[emptyRow(headers.length,t('Здесь пока нет записей.'))]));
  };
  function frame(title, detail='', actions=[], filters=[], extra=null) {
    $('m-title').textContent=title;
    $('m-detail').textContent=detail;
    $('m-actions').replaceChildren(...actions);
    $('m-filters').replaceChildren(...filters);
    $('m-extra').replaceChildren(...(extra?[extra]:[]));
    $('m-extra').hidden=!extra;
  }
  function pager(previous,next) {
    $('m-prev').disabled=!previous;
    $('m-next').disabled=!next;
  }
  function inlineInput(label,value,type='text') {
    const wrapper=element('label',null,'field');
    const node=element('input');
    node.type=type;
    node.value=value;
    node.setAttribute('aria-label',label);
    wrapper.append(element('span',label),node);
    return {wrapper,node};
  }

  function productForm(product={}) {
    const barcodeString=(product.barcodes??[]).map(row=>row.code+(row.quantityFactor===1?'':'*'+row.quantityFactor)).join('; ');
    const id=product.productId??crypto.randomUUID();
    ask(product.productId?t('Изменить карточку'):t('Новый товар'),t('Штрихкоды разделяйте точкой с запятой. Для упаковки укажите код*количество.'),
      [field('name',t('Название товара'),product.name,{maxLength:160}),
       field('unit',t('Единица'),product.unit??'piece',{options:product.productId?
         [[product.unit,product.unit==='piece'?t('Штуки'):t('Килограммы')]]:[['piece',t('Штуки')],['kg',t('Килограммы')]]}),
       field('expiry',t('Контроль срока'),product.requiresExpiry?'yes':'no',{options:[['no',t('Нет')],['yes',t('Обязателен')]]}),
       optional('sku',t('Артикул'),product.sku,64),optional('category',t('Категория'),product.category,80),
       optional('brand',t('Бренд'),product.brand,80),optional('supplier',t('Код поставщика'),product.supplierRef,160),
       optional('tax',t('Налоговый профиль'),product.taxProfile,80),optional('barcodes',t('Штрихкоды'),barcodeString,1000)],async values=>{
        if (!values.name) throw new Error(t('Укажите название товара.'));
        const barcodes=values.barcodes.split(';').filter(code=>code.trim()).map(entry=>{
          const [code, factor='1', rest]=entry.trim().split('*');
          const units=quantity(factor.trim());
          if (!/^\d{1,48}$/.test(code.trim())||!units||rest!==undefined||(values.unit==='piece'&&units%1000))
            throw new Error(t('Проверьте штрихкоды и количество в упаковке.'));
          return {code:code.trim(),quantityFactor:units/1000};
        });
        const saved=await mutate('saveProduct',id,{expectedVersion:product.version??0,name:values.name,unit:values.unit,
          requiresExpiry:values.expiry==='yes',sku:values.sku||null,category:values.category||null,brand:values.brand||null,
          supplierRef:values.supplier||null,taxProfile:values.tax||null,barcodes},'productId',id);
        view={type:'product',id:saved.productId};
        await refresh();
        say(t('Карточка товара сохранена.'));
      },t('Сохранить товар'));
  }
  function priceForm(product) {
    ask(t('Изменить цену'),product.name,[optional('amount',t('Розничная цена, сум'),product.unitPrice,18),
      field('active',t('Доступность'),product.active?'yes':'no',{options:[['yes',t('Активен')],['no',t('В архиве')]]}),
      field('reason',t('Причина изменения'))],async values=>{
        if (!values.reason) throw new Error(t('Укажите причину изменения.'));
        await mutate('savePrice',product.productId,{expectedVersion:product.priceVersion,
          unitPrice:values.amount?requireMoney(values.amount):null,active:values.active==='yes',reason:values.reason});
        await refresh(); say(t('Розничная цена сохранена.'));
      },t('Сохранить цену'));
  }
  async function promotionForm(product) {
    let current;
    await run(async()=>{
      const result = await read('promotion', product.productId);
      if (!Number.isInteger(result?.version) || typeof result.enabled !== 'boolean')
        throw new Error(t('Сервер не вернул настройки акции.'));
      current = result;
    });
    if (!current) return;
    ask(t('Настроить акцию 2+1'), t('За каждые три штуки этого товара оплачиваются две. Ручная скидка с акцией не суммируется.'),
      [field('enabled',t('Статус'),current.enabled?'yes':'no',{options:[['yes',t('Включена')],['no',t('Отключена')]]}),
       field('reason',t('Причина изменения'),'',{maxLength:500})], async values=>{
        if (!values.reason) throw new Error(t('Укажите причину изменения.'));
        const enabled = values.enabled === 'yes';
        const saved = await mutate('savePromotion',product.productId,{expectedVersion:current.version,enabled,reason:values.reason});
        if (saved?.version !== current.version + 1 || saved.enabled !== enabled)
          throw new Error(t('Сервер не вернул настройки акции.'));
        await refresh();
        say(t('Настройки акции сохранены.'));
      },t('Сохранить акцию'));
  }
  function importForm() {
    ask(t('Проверить импорт CSV'),t('Первая строка должна содержать поля в указанном порядке. Для нового товара expectedVersion равна 0. Если задана цена, expectedPriceVersion равна 1.'),
      [field('csv',t('Содержимое CSV'),csv?.text??csvHeader+'\n',{multiline:true,maxLength:48000})],async values=>{
        const result=await read('catalogImport',null,{preview:true,csv:values.csv});
        if (!result?.preview || !Array.isArray(result.products)) throw new Error(t('Не удалось проверить предварительный просмотр.'));
        csv={text:values.csv,products:result.products};
        view={type:'import'}; await refresh();
      },t('Проверить CSV'));
  }
  function confirmImport() {
    ask(t('Применить импорт'),t('Будут применены версии и цены из проверенного CSV. Строк: ')+csv.products.length+'.',[],async()=>{
      const result=await mutate('catalogImport',null,{preview:false,csv:csv.text});
      if (result?.preview!==false) throw new Error(t('Сервер не подтвердил применение импорта.'));
      csv=null; view=null; await refresh(); say(t('Каталог обновлён из CSV.'));
    },t('Применить импорт'));
  }
  async function catalog() {
    if (view?.type==='product') {
      const product=await read('product',view.id);
      const history=await read('priceHistory',view.id);
      frame(product.name,[product.sku,product.unit==='piece'?t('Штуки'):t('Килограммы'),
        product.barcodes.map(row=>row.code).join(', '),t('Версия ')+product.version, 'ID '+product.productId].filter(Boolean).join(' · '),
        [button(t('К списку'),back),button(t('Изменить карточку'),()=>productForm(product),'outline',true),
         button(t('Изменить цену'),()=>priceForm(product),'primary',true),
         ...(online() && product.unit === 'piece' ?
           [button(t('Настроить акцию 2+1'),()=>promotionForm(product),'outline',true)] : [])]);
      table([t('Версия цены'),t('Дата'),t('Розничная цена, сум'),t('Доступность'),t('Причина')],history.map(row=>
        [row.version,humanTime(row.recordedAt),amount(row.unitPrice),row.active?t('Активен'):t('В архиве'),row.reason]));
      count=0; return;
    }
    if (view?.type==='import' && csv) {
      frame(t('Предпросмотр импорта'),t('Проверено строк: ')+csv.products.length+'.',
        [button(t('К списку'),back),button(t('Применить CSV'),confirmImport,'primary',true)]);
      table([t('Товар'),t('Артикул'),t('Единица'),t('Цена, сум')],csv.products.map(row=>[row.name,row.sku,row.unit,amount(row.unitPrice)]));
      count=0; return;
    }
    const search=inlineInput(t('Поиск по товару'),query,'search');
    search.node.maxLength=100;
    const doSearch=()=>run(async()=>{query=search.node.value.trim(); offset=0; await refresh();});
    search.node.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();doSearch();}});
    const rows=await read('catalog',String(offset),query);
    count=rows.length;
    frame(tabs.catalog,t('Карточки, штрихкоды и история розничных цен.'),
      [button(t('Импорт CSV'),importForm,'outline',true),button(t('Новый товар'),()=>productForm(),'primary',true)],
      [search.wrapper,group(button(t('Найти товар'),doSearch))]);
    table([t('Товар'),t('Артикул'),t('Штрихкод'),t('Цена, сум'),t('Статус'),t('Действия')],rows.map(row=>
      [row.name,row.sku||'—',row.barcode??row.barcodes?.[0]?.code??'—',amount(row.unitPrice),
       row.active?t('Активен'):t('В архиве'),button(t('Открыть товар'),()=>open({type:'product',id:row.productId}))]));
  }
  async function proposals() {
    const rows=await read('proposals',String(offset));
    count=rows.length;
    frame(tabs.proposals,t('Предложения новых товаров, ожидающие решения администратора.'));
    function review(row,approve) {
      ask(approve?t('Утвердить предложение'):t('Отклонить предложение'),row.product.name,
        [field('reason',t('Комментарий'),'',{optional:approve})],async values=>{
          if (!approve&&!values.reason) throw new Error(t('Укажите причину отклонения.'));
          await mutate(approve?'approveProposal':'rejectProposal',row.proposalId,{reason:values.reason||null},'proposalId',row.proposalId);
          await refresh(); say(approve?t('Карточка опубликована. Розничную цену можно задать в каталоге.'):t('Предложение отклонено.'));
        },approve?t('Утвердить'):t('Отклонить'));
    }
    table([t('Товар'),t('Штрихкоды'),t('Единица'),t('Действия')],rows.map(row=>[row.product.name,
      row.product.barcodes.map(barcode=>barcode.code).join(', '),row.product.unit,
      group(button(t('Утвердить'),()=>review(row,true),'outline',true),button(t('Отклонить'),()=>review(row,false),'danger',true))]));
  }
  function supplierForm(item={}) {
    const fields=[field('code',t('Код поставщика'),item.code,{maxLength:32}),field('name',t('Название поставщика'),item.name,{maxLength:160}),
      optional('taxId',t('ИНН'),item.taxId,40),optional('phone',t('Телефон'),item.phone,60),
      optional('email',t('Электронная почта'),item.email,120),optional('paymentTerms',t('Условия оплаты'),item.paymentTerms,500),
      field('lead',t('Срок поставки, дней'),item.leadTimeDays??1,{maxLength:3}),
      field('active',t('Статус'),item.active===false?'no':'yes',{options:[['yes',t('Активен')],['no',t('Архив')]]} )];
    ask(item.code?t('Изменить поставщика'):t('Новый поставщик'),item.code?t('Код действующей карточки фиксирован.'):t('Укажите постоянный код поставщика.'),
      fields,async values=>{
        const code=requireCode(values.code);
        if (!values.name || (item.code && code!==item.code)) throw new Error(t('Проверьте название и код поставщика.'));
        await mutate('saveSupplier',code,{expectedVersion:item.version??0,name:values.name,taxId:values.taxId||null,
          phone:values.phone||null,email:values.email||null,paymentTerms:values.paymentTerms||null,
          leadTimeDays:bounded(values.lead,0,365),active:values.active==='yes'},'code',code);
        view={type:'supplier',code}; offset=0; await refresh(); say(t('Карточка поставщика сохранена.'));
      },t('Сохранить поставщика'));
  }
  function quoteForm(code,quote={}) {
    ask(quote.supplierSku?t('Изменить предложение'):t('Новая закупочная цена'),t('ID товара можно взять из карточки в каталоге.'),
      [field('sku',t('Артикул поставщика'),quote.supplierSku,{maxLength:32}),field('product',t('ID товара'),quote.productId,{maxLength:36}),
       field('package',t('Упаковка'),quote.packageUnit??t('коробка'),{maxLength:60}),field('units',t('Единиц в упаковке'),quote.unitsPerPackage??1,{maxLength:18}),
       field('price',t('Цена упаковки, сум'),quote.packagePrice,{maxLength:18}),
       field('from',t('Действует с'),quote.validFrom??storeDay(),{type:'date'}),
       optional('through',t('Действует до'),quote.validThrough,10)].map(f=>f.key==='through'?{...f,type:'date'}:f),async values=>{
        const sku=requireCode(values.sku);
        if (quote.supplierSku&&sku!==quote.supplierSku) throw new Error(t('Артикул существующего предложения фиксирован.'));
        if (!guidPattern.test(values.product)||!values.package) throw new Error(t('Проверьте ID товара и упаковку.'));
        const units=quantity(values.units);
        if (!units) throw new Error(t('Укажите количество в упаковке.'));
        const validFrom=requireDate(values.from);
        const validThrough=values.through?requireDate(values.through):null;
        if (validThrough&&validThrough<validFrom) throw new Error(t('Конечная дата раньше начальной.'));
        const history=await read('quoteHistory',code+'|'+sku);
        const current=history[0];
        if ((current?.version??0)!==(quote.version??0)) throw new Error(t('Предложение изменилось. Откройте последнюю версию из истории.'));
        await mutate('saveQuote',code+'|'+sku,{expectedVersion:quote.version??0,productId:values.product,packageUnit:values.package,
          unitsPerPackage:units/1000,packagePrice:requireMoney(values.price),validFrom,validThrough},'supplierSku',sku);
        view={type:'quoteHistory',code,sku}; offset=0; await refresh(); say(t('Закупочная цена сохранена.'));
      },t('Сохранить предложение'));
  }
  async function suppliers() {
    if (view?.type==='quoteHistory') {
      const {code,sku}=view;
      const rows=await read('quoteHistory',code+'|'+sku);
      frame(t('История предложения ')+sku,code,[button(t('К поставщику'),()=>open({type:'supplier',code})),
        ...(rows.length?[button(t('Изменить предложение'),()=>quoteForm(code,rows[0]),'primary',true)]:[])]);
      table([t('Версия'),t('ID товара'),t('Упаковка'),t('Количество'),t('Цена, сум'),t('С'),t('До')],
        rows.map(row=>[row.version,row.productId,row.packageUnit,number(row.unitsPerPackage),amount(row.packagePrice),date(row.validFrom),date(row.validThrough)]));
      count=0; return;
    }
    if (view?.type==='supplier') {
      const item=await read('supplier',view.code);
      const rows=await read('quotes',item.code,String(offset));
      count=rows.length;
      frame(item.name,item.code+' · '+(item.active?t('Активен'):t('В архиве'))+t(' · срок поставки ')+item.leadTimeDays+t(' дн.'),
        [button(t('К списку'),back),button(t('Изменить поставщика'),()=>supplierForm(item),'outline',true),
         ...(item.active?[button(t('Добавить закупочную цену'),()=>quoteForm(item.code),'primary',true)]:[])]);
      table([t('Артикул'),t('ID товара'),t('Упаковка'),t('Единиц'),t('Цена, сум'),t('Действия')],
        rows.map(row=>[row.supplierSku,labelId(row.productId),row.packageUnit,number(row.unitsPerPackage),amount(row.packagePrice),
          group(button(t('История предложения'),()=>open({type:'quoteHistory',code:item.code,sku:row.supplierSku})),
          button(t('Рассчитать закупку'),()=>recommendForm(item.code,row.supplierSku)))]));
      return;
    }
    const rows=await read('suppliers',String(offset));
    count=rows.length;
    frame(tabs.suppliers,t('Карточки поставщиков, цены, упаковки и сроки поставки.'),
      [button(t('Новый поставщик'),()=>supplierForm(),'primary',true)]);
    table([t('Код'),t('Поставщик'),t('Действия')],rows.map(row=>[row.code,row.name,
      button(t('Открыть поставщика'),()=>open({type:'supplier',code:row.code}))]));
  }

  function recommendForm(code,sku,order=null) {
    const initial=order?.replenishment?.settings;
    ask(t('Рассчитать закупку'),code+' · '+sku,[field('history',t('История, дней'),initial?.historyDays??28,{maxLength:3}),
      field('minimum',t('Минимум дней истории'),initial?.minimumHistoryDays??7,{maxLength:3}),field('coverage',t('Покрытие, дней'),initial?.coverageDays??5,{maxLength:3}),
      field('safety',t('Страховой запас'),initial?.safetyStock??0,{maxLength:18}),field('minimumPackages',t('Минимум упаковок'),initial?.minimumPackages??1,{maxLength:6}),
      optional('manual',t('Ручной спрос в день'),initial?.manualDailyDemand,18),
      optional('lead',t('Срок поставки вручную, дней'),initial?.leadTimeDays,3),
      optional('weekdays',t('Дни заказа (1 — пн, 7 — вс)'),initial?.orderWeekdays?.join(','),30),
      field('cutoff',t('Приём заказов до'),initial?.orderCutoffTime?.slice(0,5),{type:'time',optional:true})],async values=>{
        const safetyStock=quantity(values.safety);
        const manualDailyDemand=values.manual?quantity(values.manual):null;
        if (safetyStock===null || (values.manual&&manualDailyDemand===null)) throw new Error(t('Проверьте запас и ручной спрос.'));
        const historyDays=bounded(values.history,1,90);
        const orderWeekdays=values.weekdays ? values.weekdays.split(',').map(day=>bounded(day.trim(),1,7)) : null;
        if (orderWeekdays && new Set(orderWeekdays).size !== orderWeekdays.length) throw new Error(t('Дни заказа не должны повторяться.'));
        const settings={historyDays,minimumHistoryDays:bounded(values.minimum,1,historyDays),coverageDays:bounded(values.coverage,1,90),
          safetyStock:safetyStock/1000,minimumPackages:bounded(values.minimumPackages,1,100000),
          manualDailyDemand:manualDailyDemand===null?null:manualDailyDemand/1000,
          leadTimeDays:values.lead?bounded(values.lead,0,365):null,orderWeekdays,orderCutoffTime:values.cutoff?values.cutoff+':00':null};
        const recommendation=await read('recommend',code+'|'+sku,settings);
        if (!recommendation?.productName) throw new Error(t('Не удалось прочитать расчёт.'));
        tab='drafts'; view={type:'recommendation',recommendation,order}; offset=0; await refresh();
      },t('Рассчитать'));
  }
  function showRecommendation(recommendation,order) {
    const {demand,projection,deliveries}=recommendation;
    const explanation=element('div');
    const warning=demand?.insufficientHistory?t('Недостаточно истории продаж. '):'';
    explanation.append(element('p',warning+(demand?.source==='manual'?t('Использован ручной спрос.'):
      t('Расчёт основан на доступной истории продаж.'))));
    const actions=[button(order?t('К заказу'):t('К заказам'),order?()=>open({type:'draft',id:order.orderId}):back)];
    if (recommendation.draft) actions.push(action(order?t('Обновить черновик'):t('Создать черновик'),async()=>{
      const id=order?.orderId??crypto.randomUUID();
      const body={...recommendation.draft,expectedRevision:order?.revision??0};
      const saved=await mutate('saveDraft',id,body,'orderId',id);
      view={type:'draft',id:saved.orderId}; await refresh(); say(t('Черновик создан по рекомендации.'));
    },'primary',true));
    frame(t('Рекомендация · ')+recommendation.productName,
      [date(recommendation.arrivalDate),recommendation.timeZone,recommendation.calculationVersion].filter(Boolean).join(' · '),actions,[],explanation);
    const risk={normal:t('Стабильный остаток'),shortage:t('Риск дефицита'),critical:t('Критический риск'),belowSafety:t('Ниже страхового запаса')};
    table([t('Показатель'),t('Значение')],[
      [t('Дата заказа'),date(recommendation.orderDate)],[t('Дата поставки'),date(recommendation.arrivalDate)],
      [t('Доступный остаток'),number(recommendation.sellableStock)],[t('Спрос в день'),number(demand?.unitsPerDay)],
      [t('Дней истории'),number(demand?.usableDays)],[t('Остаток к поставке'),number(projection?.stockAtArrival)],
      [t('Целевой запас'),number(projection?.targetQuantity)],[t('Риск'),risk[projection?.risk]??projection?.risk??'—'],
      [t('Ожидается по поставкам'),number(deliveries?.reduce((sum,row)=>sum+Number(row.remainingQuantity),0))],
      [t('Рекомендовано упаковок'),number(recommendation.suggestedPackages)],[t('Рекомендовано единиц'),number(recommendation.suggestedQuantity)],
    ]);
    count=0;
  }
  function chooseSupplier() {
    ask(t('Новый заказ'),t('Введите код действующего поставщика. В следующем окне можно выбрать несколько предложений.'),
      [field('code',t('Код поставщика'),'',{maxLength:32})],async values=>{
        const code=requireCode(values.code);
        const supplier=await read('supplier',code);
        if (!supplier.active) throw new Error(t('Поставщик находится в архиве.'));
        view={type:'draftEdit',id:crypto.randomUUID(),code,revision:0,lines:[],arrival:storeDay()};
        await refresh();
      },t('Выбрать поставщика'));
  }
  async function draftEditor(edit) {
    const quotes=[];
    for (let start=0;start<100;start+=25) {
      const page=await read('quotes',edit.code,String(start));
      quotes.push(...page);
      if (page.length<25) break;
    }
    const bySku=new Map(quotes.map(quote=>[quote.supplierSku,quote]));
    const missing=edit.lines.filter(line=>!bySku.has(line.supplierSku));
    const dateInput=inlineInput(t('Ожидаемая дата поставки'),edit.arrival,'date');
    const inputs=[];
    const quantities=new Map(edit.lines.map(row=>[row.supplierSku,row.packages]));
    const rows=quotes.map(quote=>{
      const {node}=inlineInput(t('Упаковок · ')+quote.supplierSku,quantities.get(quote.supplierSku)??'');
      node.inputMode='numeric'; node.maxLength=6;
      inputs.push({quote,node});
      return [quote.supplierSku,quote.packageUnit,number(quote.unitsPerPackage),amount(quote.packagePrice),node];
    });
    const save=action(t('Сохранить заказ'),async()=>{
      const expectedDeliveryDate=requireDate(dateInput.node.value);
      if (expectedDeliveryDate<storeDay()) throw new Error(t('Укажите дату поставки не раньше сегодняшнего дня.'));
      const items=inputs.filter(({node})=>node.value.trim() && node.value.trim()!=='0').map(({quote,node})=>({
        supplierSku:quote.supplierSku,priceVersion:quote.version,packages:bounded(node.value.trim(),1,100000)}));
      if (!items.length) throw new Error(t('Выберите хотя бы одно предложение и укажите количество упаковок.'));
      await mutate('saveDraft',edit.id,{expectedRevision:edit.revision,supplierCode:edit.code,expectedDeliveryDate,items},
        'orderId',edit.id);
      view={type:'draft',id:edit.id}; await refresh(); say(t('Черновик заказа сохранён.'));
    },'primary',true);
    const detail=missing.length?t('При сохранении недействующие позиции будут исключены: ')+missing.map(row=>row.supplierSku).join(', ')+'.':
      t('Поставщик ')+edit.code+t('. Укажите количество упаковок по каждой позиции.');
    frame(edit.revision?t('Изменить заказ'):t('Черновик заказа'),detail,
      [button(t('Отмена'),()=>edit.revision?open({type:'draft',id:edit.id}):back()),save],[dateInput.wrapper]);
    table([t('Артикул поставщика'),t('Упаковка'),t('Единиц в упаковке'),t('Цена упаковки, сум'),t('Упаковок в заказе')],rows);
    count=0;
  }
  async function drafts() {
    if (view?.type==='recommendation') return showRecommendation(view.recommendation,view.order);
    if (view?.type==='draftEdit') return draftEditor(view);
    if (view?.type==='draft') {
      const item=await read('draft',view.id);
      const controls=[button(t('К заказам'),back)];
      if (item.stage!=='sent' && item.replenishment) controls.push(button(t('Пересчитать заказ'),
        ()=>recommendForm(item.supplierCode,item.lines[0].supplierSku,item),'outline',true));
      if (item.stage!=='sent' && !item.replenishment) controls.push(button(t('Изменить заказ'),()=>open({type:'draftEdit',id:item.orderId,code:item.supplierCode,
        revision:item.revision,lines:item.lines,arrival:item.expectedDeliveryDate}),'outline',true));
      if (item.stage==='draft') controls.push(button(t('Утвердить заказ'),()=>ask(t('Утвердить заказ'),
        t('Версия ')+item.revision+' · '+amount(item.total)+t(' сум.'),[],async()=>{
          await mutate('approveDraft',item.orderId,{expectedRevision:item.revision},'orderId',item.orderId);
          await refresh(); say(t('Версия заказа утверждена.'));
        },t('Утвердить')),'primary',true));
      if (item.stage==='approved') controls.push(button(t('Отметить отправку'),()=>ask(t('Отметить отправку'),
        t('Введите номер или ссылку на уже отправленный заказ.'),[field('reference',t('Подтверждение отправки'),'',{maxLength:300})],async values=>{
          if (!values.reference) throw new Error(t('Укажите подтверждение отправки.'));
          await mutate('markSent',item.orderId,{expectedRevision:item.revision,reference:values.reference},'orderId',item.orderId);
          await refresh(); say(t('Отправка зарегистрирована. Заказ доступен приёмщику.'));
        },t('Зарегистрировать отправку')),'primary',true));
      frame(t('Заказ ')+labelId(item.orderId),[item.supplierCode,stage[item.stage], t('Версия ')+item.revision,
        t('Поставка ')+date(item.expectedDeliveryDate), t('Всего ')+amount(item.total)+t(' сум'),item.dispatchReference].filter(Boolean).join(' · '),controls);
      table([t('Артикул'),t('Упаковок'),t('Единиц'),t('Цена упаковки, сум'),t('Итого, сум')],item.lines.map(line=>
        [line.supplierSku,line.packages,number(line.baseQuantity),amount(line.packagePrice),amount(line.total)]));
      count=0; return;
    }
    const rows=await read('drafts',String(offset));
    count=rows.length;
    frame(tabs.drafts,t('Черновики, утверждение версии и регистрация отправки поставщику.'),
      [button(t('Новый заказ'),chooseSupplier,'primary',true)]);
    table([t('Заказ'),t('Поставщик'),t('Дата поставки'),t('Версия'),t('Статус'),t('Сумма, сум'),t('Действия')],rows.map(row=>
      [labelId(row.orderId),row.supplierCode,date(row.expectedDeliveryDate),row.revision,stage[row.stage],amount(row.total),
        button(t('Открыть заказ'),()=>open({type:'draft',id:row.orderId}))]));
  }

  const reportFields={
    sales:[['saleCount',t('Чеков')],['cashSales',t('Наличные продажи')],['manualCashlessSales',t('Безналичные продажи')],
      ['refunds',t('Возвраты')],['netSales',t('Продажи за вычетом возвратов')],['cashIn',t('Внесения')],['cashOut',t('Изъятия')]],
    profit:[['sales',t('Продажи')],['refunds',t('Возвраты')],['netRevenue',t('Чистая выручка')],
      ['costOfGoodsSold',t('Себестоимость')],['grossProfit',t('Валовая прибыль')],['grossMarginPercent',t('Маржа, %')],['inventoryExpenses',t('Потери, сум')]],
    'stock-movements':[['occurredAt',t('Дата')],['productName',t('Товар')],['kind',t('Операция')],['stockDelta',t('Остаток, изменение')],
      ['quarantineDelta',t('Карантин, изменение')],['reason',t('Причина')]],
    'stock-losses':[['occurredAt',t('Дата')],['productName',t('Товар')],['kind',t('Операция')],['quantity',t('Количество')],['cost',t('Стоимость потери')]],
    audit:[['occurredAt',t('Дата')],['action',t('Действие')],['staffId',t('Сотрудник')],['objectId',t('Объект')],['requestId',t('Запрос')]],
  };
  function reportRows(data,kind,format=true) {
    const fields=reportFields[kind];
    const convert=(value,key)=>{
      if (!format) return value??'';
      if (key==='occurredAt') return humanTime(value,data.timeZone);
      if (['staffId','objectId','requestId'].includes(key)) return value??'—';
      if (typeof value==='number') return number(value);
      return value??'—';
    };
    if (data.days) return {headers:[t('Период'),...fields.map(([,label])=>label)],
      rows:[[t('Итого'),...fields.map(([key])=>convert(data.total[key],key))],
        ...data.days.map(row=>[format?date(row.date):row.date,...fields.map(([key])=>convert(row.amounts[key],key))])]};
    return {headers:fields.map(([,label])=>label),
      rows:data.items.map(row=>fields.map(([key])=>convert(row[key],key)))};
  }
  const csvCell=value=>{
    let text=String(value??'');
    if (typeof value==='string' && /^[\s]*[=+@-]/.test(text)) text="'"+text;
    return '"'+text.replace(/"/g,'""')+'"';
  };
  async function exportReport() {
    if (!period || !reportData || reportKind==='audit') return;
    if (online()) {
      if (await read('reportCsv',reportKind,period)) say(t('Отчёт CSV сохранён.'));
      return;
    }
    const data=reportRows(reportData,reportKind,false);
    const lines=[...data.rows];
    if (reportData.items) {
      const first=await read('report',reportKind,{...period,after:null});
      lines.splice(0,lines.length,...reportRows(first,reportKind,false).rows);
      let cursor=first.nextCursor;
      while (cursor && lines.length<10000) {
        const next=await read('report',reportKind,{...period,after:cursor});
        lines.push(...reportRows(next,reportKind,false).rows);
        cursor=next.nextCursor;
      }
      if (cursor) throw new Error(t('Уменьшите период для выгрузки.'));
    }
    const output=[data.headers,...lines].map(row=>row.map(csvCell).join(';')).join('\r\n');
    if (bridge) {
      if (await bridge('store.management.localCsv',null,output)) say(t('Отчёт CSV сохранён.'));
    } else {
      const file=URL.createObjectURL(new Blob(['\uFEFF',output],{type:'text/csv;charset=utf-8'}));
      const link=element('a');
      link.href=file; link.download=reportKind+'.csv'; link.click();
      setTimeout(()=>URL.revokeObjectURL(file),1000);
    }
  }
  async function buildReport(after=null) {
    reportData=null;
    reportData=await read('report',reportKind,{...period,after});
    showReports();
  }
  function showReports() {
    const from=inlineInput(t('Период с'),period?.from??storeDay(),'date');
    const to=inlineInput(t('Период по'),period?.to??storeDay(),'date');
    const kind=element('select');
    kind.setAttribute('aria-label',t('Тип отчёта'));
    for (const [value,label] of reports) {
      const option=element('option',label); option.value=value; kind.append(option);
    }
    kind.value=reportKind;
    const kindLabel=element('label',null,'field');
    kindLabel.append(element('span',t('Тип отчёта')),kind);
    const refreshReport=action(t('Сформировать отчёт'),async()=>{
      const dates={from:requireDate(from.node.value),to:requireDate(to.node.value)};
      if (dates.to<dates.from) throw new Error(t('Дата окончания раньше начала периода.'));
      period=dates; reportKind=kind.value; cursors=[null]; cursorIndex=0; reportData=null;
      showReports();
      await buildReport();
    },'primary');
    const details=reportData?[
      date(period.from)+' — '+date(period.to),reportData.timeZone,
      reportData.generatedAt?t('Обновлено ')+humanTime(reportData.generatedAt,reportData.timeZone):'',
      reportKind==='profit'&&reportData.total.costOfGoodsSold==null?t('Маржа недоступна без полной себестоимости.'):'',
    ].filter(Boolean).join(' · '):t('Выберите отчёт и период.');
    frame(tabs.reports,details,reportData&&reportKind!=='audit'?[action(t('Выгрузить CSV'),exportReport)]:[],
      [kindLabel,from.wrapper,to.wrapper,group(refreshReport)]);
    if (reportData) {
      const {headers,rows}=reportRows(reportData,reportKind);
      table(headers,rows);
    } else table([t('Отчёт')],[]);
    pager(cursorIndex>0,Boolean(reportData?.nextCursor));
  }

  async function refresh() {
    if (!admin()) return;
    for (const control of document.querySelectorAll('[data-m-tab]')) {
      const selected=control.dataset.mTab===tab;
      control.classList.toggle('selected',selected);
      control.setAttribute('aria-pressed',String(selected));
    }
    if (tab==='reports') { showReports(); return; }
    await {catalog,proposals,suppliers,drafts}[tab]();
    pager(offset>0,count>=25&&offset<10000);
  }
  function reset() {
    tab='catalog'; view=null; offset=count=0; query=''; csv=null; period=reportData=null;
    cursors=[null]; cursorIndex=0; reportKind='sales';
  }
  for (const control of document.querySelectorAll('[data-m-tab]')) control.addEventListener('click',()=>run(async()=>{
    tab=control.dataset.mTab; view=null; offset=0; say(''); await refresh();
  }));
  for (const [id,delta] of [['m-prev',-1],['m-next',1]]) $(id).addEventListener('click',()=>run(async()=>{
    if (tab==='reports') {
      if (delta>0) cursors[cursorIndex+1]=reportData.nextCursor;
      cursorIndex+=delta;
      await buildReport(cursors[cursorIndex]);
    } else {
      offset=Math.max(0,offset+delta*25);
      await refresh();
    }
  }));
  return {refresh,reset,lock(value) {
    locked=value;
    for (const control of document.querySelectorAll('[data-m-mutation]')) control.disabled=value;
  }};
}
