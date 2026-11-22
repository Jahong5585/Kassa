"""Run after standard disposable fixtures, all migrations and restricted runtime grants."""
import runpy,uuid,copy,concurrent.futures,json,urllib.request,urllib.error,os
from datetime import date,timedelta
from pathlib import Path
c=runpy.run_path(str(Path(__file__).with_name('quick_receiving_api.py')))
call,sql,route,session,line,STORE,BASE=(c[k] for k in ['call','sql','route','session','line','STORE','BASE'])
# Further attempts to create the same no-code item must not generate a second card.
duplicate=line('Yangi non');problem=call(route,{'operationId':str(uuid.uuid4()),'items':[duplicate]},expected=409,session=session)
assert problem['code']=='receiving_existing_product_select';assert sql(f"SELECT count(*) FROM products WHERE id='{duplicate['productId']}'")=='0'
# The shared normalized-name lock also protects simultaneous first-time additions.
def new_name(_):
 body={'operationId':str(uuid.uuid4()),'items':[line('Bir xil parallel nom')]}
 headers={'Content-Type':'application/json','Authorization':'Bearer '+c['token'](),'X-Operator-Session':session,'X-Active-Role':'receiver'}
 try:
  with urllib.request.urlopen(urllib.request.Request(BASE+route,data=json.dumps(body).encode(),headers=headers),timeout=20) as r:return r.status
 except urllib.error.HTTPError as e:return e.code
with concurrent.futures.ThreadPoolExecutor(2) as pool:statuses=list(pool.map(new_name,range(2)))
assert sorted(statuses)==[200,409],statuses
assert sql("SELECT count(*) FROM products WHERE name='Bir xil parallel nom'")=='1'
# Cost stays on each exact lot even when the next purchase is more expensive.
old=line('Cost separation product');old.update(quantity=5,unitCost=8000,salePrice=12000,expiryDate=str(date.today()+timedelta(days=7)));old['product']['requiresExpiry']=True
call(route,{'operationId':str(uuid.uuid4()),'items':[old]},session=session)
card=call(route+'/'+old['productId'],session=session)['product'];shift=c['shift']
def sell(lot,quantity,price_version,tendered):
 body={'operationId':str(uuid.uuid4()),'shiftId':shift,'method':'cash','tendered':tendered,'items':[{'productId':old['productId'],'lotId':lot,'quantity':quantity,'priceVersion':price_version}]}
 result=call('/v1/sales',body,role='cashier',session=session,register=True)
 return body,result
before_sale,before=sell(old['lotId'],1,card['priceVersion'],12000)
original=sql(f"SELECT unit_price||'/'||line_total||'/'||inventory_cost FROM sale_lines WHERE sale_id='{before_sale['operationId']}'")
new=copy.deepcopy(old);new.update(lotId=str(uuid.uuid4()),expectedVersion=card['version'],expectedPriceVersion=card['priceVersion'],quantity=10,unitCost=9000,salePrice=13000,expiryDate=str(date.today()+timedelta(days=30)))
call(route,{'operationId':str(uuid.uuid4()),'items':[new]},session=session)
assert sql(f"SELECT sum(quantity)||'/'||sum(inventory_value) FROM stock_lots WHERE product_id='{old['productId']}'")=='14.000/122000.00'
assert sql(f"SELECT unit_price||'/'||line_total||'/'||inventory_cost FROM sale_lines WHERE sale_id='{before_sale['operationId']}'")==original=='12000.00/12000.00/8000.00'
lots=call(f'/v1/stores/{STORE}/stock/lots?productId={old["productId"]}',role='admin',session=session)
assert len(lots)==2,lots
assert {r['unitCost'] for r in lots}=={8000,9000},lots
assert all(r['receivedAt'] for r in lots),lots
cash=call('/v1/catalog/products/'+old['productId'],role='cashier',session=session,register=True)
assert cash['unitPrice']==13000 and cash['lots'][0]['lotId']==old['lotId'],'Earlier expiry is offered first'
purchase_history=call(f'/v1/stores/{STORE}/catalog/products/{old["productId"]}/purchase-price-history',role='admin',session=session)
assert purchase_history[0]['unitCost']==9000 and purchase_history[0]['previousUnitCost']==8000,purchase_history
assert purchase_history[0]['changeAmount']==1000 and purchase_history[0]['changePercent']==12.5,purchase_history
price=call(route+'/'+old['productId'],session=session)['product']['priceVersion']
mixed={'operationId':str(uuid.uuid4()),'shiftId':shift,'method':'cash','tendered':65000,'items':[{'productId':old['productId'],'lotId':old['lotId'],'quantity':4,'priceVersion':price},{'productId':old['productId'],'lotId':new['lotId'],'quantity':1,'priceVersion':price}]}
call('/v1/sales',mixed,role='cashier',session=session,register=True)
assert sql(f"SELECT sum(inventory_cost) FROM sale_lines WHERE sale_id='{mixed['operationId']}'")=='41000.00'
assert sql(f"SELECT quantity||'/'||inventory_value FROM stock_lots WHERE id='{new['lotId']}'")=='9.000/81000.00'
# FIFO is the second criterion when expiry dates are equal. The same order is returned by
# direct cashier lookup and the durable changes feed, and exact lot IDs remain untouched.
cursor=int(sql('SELECT coalesce(max(cursor_id),0) FROM catalog_change_feed'))
fifo_old=line('FIFO equal expiry');fifo_old.update(quantity=5,unitCost=8000,salePrice=11000,expiryDate=str(date.today()+timedelta(days=20)));fifo_old['product']['requiresExpiry']=True
call(route,{'operationId':str(uuid.uuid4()),'items':[fifo_old]},session=session)
fifo_card=call(route+'/'+fifo_old['productId'],session=session)['product']
fifo_new=copy.deepcopy(fifo_old);fifo_new.update(lotId=str(uuid.uuid4()),quantity=10,unitCost=9000,expectedVersion=fifo_card['version'],expectedPriceVersion=fifo_card['priceVersion'])
call(route,{'operationId':str(uuid.uuid4()),'items':[fifo_new]},session=session)
fifo_cash=call('/v1/catalog/products/'+fifo_old['productId'],role='cashier',session=session,register=True)
assert [r['lotId'] for r in fifo_cash['lots'][:2]]==[fifo_old['lotId'],fifo_new['lotId']],fifo_cash
changes=call(f'/v1/catalog/changes?cursor={cursor}&limit=100',role='cashier',session=session,register=True)
changed=next(i['product'] for i in changes['items'] if i['product']['productId']==fifo_old['productId'])
assert [r['lotId'] for r in changed['lots'][:2]]==[fifo_old['lotId'],fifo_new['lotId']],changed
fifo_price=call(route+'/'+fifo_old['productId'],session=session)['product']['priceVersion']
fifo_sale={'operationId':str(uuid.uuid4()),'shiftId':shift,'method':'cash','tendered':110000,'items':[{'productId':fifo_old['productId'],'lotId':fifo_old['lotId'],'quantity':5,'priceVersion':fifo_price},{'productId':fifo_old['productId'],'lotId':fifo_new['lotId'],'quantity':5,'priceVersion':fifo_price}]}
call('/v1/sales',fifo_sale,role='cashier',session=session,register=True)
assert sql(f"SELECT sum(inventory_cost) FROM sale_lines WHERE sale_id='{fifo_sale['operationId']}'")=='85000.00'
assert sql(f"SELECT sum(quantity)||'/'||sum(inventory_value) FROM stock_lots WHERE product_id='{fifo_old['productId']}'")=='5.000/45000.00'
depleted=call(f'/v1/stores/{STORE}/stock/lots?productId={fifo_old["productId"]}',role='admin',session=session)
assert len(depleted)==2 and next(r for r in depleted if r['lotId']==fifo_old['lotId'])['active'] is False,depleted
# Start from the inventory section with a product scope. Uncounted fact remains null.
invroute=f'/v1/stores/{STORE}/stock/inventories';iid=str(uuid.uuid4())
call(invroute,{'inventoryId':iid,'reason':'Count cost product','scope':{'kind':'product','productId':old['productId']}},role='admin',session=session)
doc=call(invroute+'/'+iid,role='admin',session=session)
assert {r['expectedStock'] for r in doc['lines']}=={0,9};assert all(r['countedStock'] is None for r in doc['lines'])
counts=[{'productId':r['productId'],'lotId':r['lotId'],'lotRevision':r['lotRevision'],'stockQuantity':8 if r['lotId']==new['lotId'] else 0,'quarantineQuantity':0} for r in doc['lines']]
counted=call(invroute+'/'+iid+'/count',{'expectedVersion':doc['version'],'items':counts},role='admin',session=session)
assert next(r for r in counted['lines'] if r['lotId']==new['lotId'])['stockDifference']==-1
posted=call(invroute+'/'+iid+'/post',{'expectedVersion':counted['version']},role='admin',session=session)
assert sql(f"SELECT quantity||'/'||inventory_value FROM stock_lots WHERE id='{new['lotId']}'")=='8.000/72000.00'
assert call(invroute+'/'+iid+'/post',{'expectedVersion':counted['version']},role='admin',session=session)==posted
# A sale after a count forces a recount, and cannot be overwritten by a stale inventory.
iid2=str(uuid.uuid4());call(invroute,{'inventoryId':iid2,'reason':'Concurrent sale','scope':{'kind':'product','productId':old['productId']}},role='admin',session=session)
def count_doc(doc):
 return call(invroute+'/'+iid2+'/count',{'expectedVersion':doc['version'],'items':[{'productId':r['productId'],'lotId':r['lotId'],'lotRevision':r['lotRevision'],'stockQuantity':r['expectedStock'],'quarantineQuantity':r['expectedQuarantine']} for r in doc['lines']]},role='admin',session=session)
doc=count_doc(call(invroute+'/'+iid2,role='admin',session=session));sell(new['lotId'],1,price,13000)
problem=call(invroute+'/'+iid2+'/post',{'expectedVersion':doc['version']},expected=409,role='admin',session=session);assert problem['code']=='inventory_lot_changed'
assert sql(f"SELECT quantity FROM stock_lots WHERE id='{new['lotId']}'")=='7.000'
fresh=call(invroute+'/'+iid2,role='admin',session=session);assert any(r['countStatus']=='stale' for r in fresh['lines'])
for r in fresh['lines']:
 if r['lotId']==new['lotId']:r['expectedStock']=7
recounted=count_doc(fresh)
third=copy.deepcopy(new);template=call(route+'/'+old['productId'],session=session)['product'];third.update(lotId=str(uuid.uuid4()),quantity=2,unitCost=9500,expectedVersion=template['version'],expectedPriceVersion=template['priceVersion'])
call(route,{'operationId':str(uuid.uuid4()),'items':[third]},session=session)
problem=call(invroute+'/'+iid2+'/post',{'expectedVersion':recounted['version']},expected=409,role='admin',session=session);assert problem['code']=='inventory_scope_changed'
refreshed=call(invroute+'/'+iid2+'/refresh',{},role='admin',session=session);assert len(refreshed['lines'])==3
refreshed=call(invroute+'/'+iid2,role='admin',session=session)
thirdline=next(r for r in refreshed['lines'] if r['lotId']==third['lotId']);assert thirdline['expectedStock']==2 and thirdline['countStatus']=='pending'
# Audit resolves full names itself, including inactive staff, scoped to the correct organisation.
org='00000000-0000-0000-0000-000000000001';owner='00000000-0000-0000-0000-000000003050';cashier='00000000-0000-0000-0000-000000000031'
sql(f"UPDATE staff SET first_name='Jahongir',last_name='Orifjonov',display_name=id::text WHERE (org_id,id)=('{org}','{owner}'); UPDATE staff SET first_name='Davronbek',last_name='Aliyev',active=false WHERE (org_id,id)=('{org}','{cashier}'); INSERT INTO staff(org_id,id,subject,role,first_name,last_name) VALUES('00000000-0000-0000-0000-000000000002','{cashier}','foreign-name-collision','cashier','SECRET','FOREIGN'); INSERT INTO audit_events(org_id,store_id,register_id,staff_id,action,object_id,request_id) VALUES('{org}','{STORE}','00000000-0000-0000-0000-000000000021','{cashier}','fixture.inactive_staff','{cashier}','stock-audit-test');")
audit=call(f'/v1/stores/{STORE}/audit?from={date.today()}&to={date.today()}&limit=100',role='admin',session=session)
assert any(r['staffId']==owner and r['staffName']=='Jahongir Orifjonov' and r['staffIsOwner'] for r in audit['items']),audit
inactive=call(f'/v1/stores/{STORE}/audit?from={date.today()}&to={date.today()}&action=fixture.inactive_staff',role='admin',session=session)
assert len(inactive['items'])==1 and inactive['items'][0]['staffId']==cashier and inactive['items'][0]['staffName']=='Davronbek Aliyev',inactive
assert 'SECRET' not in json.dumps(inactive)
# Unknown cost remains null; a product without a retail price is not called available for sale.
unpriced=line('Unknown cost and no retail');unpriced.update(unitCost=None,salePrice=None)
call(route,{'operationId':str(uuid.uuid4()),'items':[unpriced]},session=session)
row=call(f'/v1/stores/{STORE}/stock/lots?productId={unpriced["productId"]}',role='admin',session=session)[0]
assert row['inventoryValue'] is None and row['unitCost'] is None and row['availableQuantity']==0,row
print('PASS PostgreSQL17: no-code duplicate and concurrent name guards; separate old/new lot costs, unchanged historical sale, FEFO offer order, new retail price, mixed-lot COGS; scoped inventory start/count/difference/post/replay, concurrent sale rejection, new-lot membership refresh; full audit names/owner/inactive staff and tenant isolation')
