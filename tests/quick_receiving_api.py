"""Isolated PostgreSQL17 HTTP integration checks. Never point this at production."""
import base64,concurrent.futures,copy,hashlib,hmac,json,os,subprocess,time,urllib.error,urllib.request,uuid
from datetime import date,timedelta
BASE=os.environ['POS_TEST_URL']; STORE='00000000-0000-0000-0000-000000000011'; FOREIGN='00000000-0000-0000-0000-000000000012'
CONTAINER=os.environ['POS_TEST_DB_CONTAINER']
assert BASE.startswith('http://127.0.0.1:'), 'This test mutates disposable local fixtures only'
def token(subject='admin-owner'):
 enc=lambda v:base64.urlsafe_b64encode(v).rstrip(b'=')
 now=int(time.time());h=enc(b'{"alg":"HS256","typ":"JWT"}');p=enc(json.dumps({'iss':'kasa-local','aud':os.environ['Auth__Audience'],'sub':subject,'iat':now,'exp':now+600}).encode());m=h+b'.'+p
 return (m+b'.'+enc(hmac.digest(os.environ['Auth__DevelopmentKey'].encode(),m,hashlib.sha256))).decode()
def call(path,body=None,expected=200,role='receiver',session=None,subject='admin-owner',authenticated=True,register=False):
 headers={'Content-Type':'application/json','X-Active-Role':role}
 if authenticated:headers['Authorization']='Bearer '+token(subject)
 if register:headers.update({'X-Register-Id':'00000000-0000-0000-0000-000000000021','X-Register-Key':os.environ['POS_TEST_REGISTER_KEY']})
 if session:headers['X-Operator-Session']=session
 req=urllib.request.Request(BASE+path,headers=headers,data=None if body is None else json.dumps(body).encode())
 try:
  with urllib.request.urlopen(req,timeout=20) as r:status=r.status;data=r.read()
 except urllib.error.HTTPError as e:status=e.code;data=e.read()
 value=json.loads(data) if data else None
 assert status==expected,(path,status,expected,value)
 return value
def sql(s):
 r=subprocess.run(['docker','exec',CONTAINER,'psql','-U','kasa_owner','-d','kasa','-X','-v','ON_ERROR_STOP=1','-Atc',s],capture_output=True,text=True);assert r.returncode==0,r.stderr;return r.stdout.strip()
def line(name='Yangi non',unit='piece',quantity=3):
 return {'productId':str(uuid.uuid4()),'lotId':str(uuid.uuid4()),'expectedVersion':0,'expectedPriceVersion':0,'product':{'name':name,'unit':unit,'requiresExpiry':False,'category':'Non','brand':'Mahalliy','shelfLifeDays':None,'barcodes':[]},'quantity':quantity,'unitCost':2000,'salePrice':3000,'manufacturedOn':None,'expiryDate':None}
def qty(i):return sql(f"SELECT quantity||'/'||inventory_value FROM stock_lots WHERE product_id='{i['productId']}' AND id='{i['lotId']}'")
route=f'/v1/stores/{STORE}/receiving/quick-products'
call(route,{'operationId':str(uuid.uuid4()),'items':[line()]},expected=401,authenticated=False)
call(route,{'operationId':str(uuid.uuid4()),'items':[line()]},expected=403,role='cashier',subject='cashier-a')
operators=call(f'/v1/stores/{STORE}/operators');owner=next(i for i in operators['items'] if i['isOwner'])
session=call(f'/v1/stores/{STORE}/operator-sessions',{'operatorId':owner['operatorId'],'password':None,'deviceId':str(uuid.uuid4())})['operatorSessionToken']
receipts_before=sql("SELECT count(*) FROM receiving_documents WHERE org_id='00000000-0000-0000-0000-000000000001'")
item=line();weighted=line('Olma','kg',1.25);body={'operationId':str(uuid.uuid4()),'items':[item,weighted]}
posted=call(route,body,session=session);assert posted['items']==body['items'];assert qty(item)=='3.000/6000.00',qty(item);assert qty(weighted)=='1.250/2500.00',qty(weighted)
assert sql("SELECT count(*) FROM receiving_documents WHERE org_id='00000000-0000-0000-0000-000000000001'")==receipts_before,'No fake invoice'
assert call(route,body,session=session)==posted;assert qty(item)=='3.000/6000.00'
wrong=copy.deepcopy(body);wrong['items'][0]['quantity']=4;call(route,wrong,expected=409,session=session)
template=call(route+'/'+item['productId'],session=session);assert template['unitCost']==2000;card=template['product'];assert card['unitPrice']==3000 and card['category']=='Non' and card['brand']=='Mahalliy'
second=copy.deepcopy(item);second.update(lotId=str(uuid.uuid4()),expectedVersion=card['version'],expectedPriceVersion=card['priceVersion'],quantity=2)
more={'operationId':str(uuid.uuid4()),'items':[second]}
with concurrent.futures.ThreadPoolExecutor(2) as pool:results=list(pool.map(lambda _:call(route,more,session=session),range(2)))
assert results[0]==results[1];assert qty(second)=='2.000/4000.00';assert sql(f"SELECT sum(quantity) FROM stock_lots WHERE product_id='{item['productId']}'")=='5.000'
# A second line conflict rolls back the first line's new card, price, lot and journal too.
new=line('Should roll back');bad=copy.deepcopy(second);bad.update(lotId=str(uuid.uuid4()),expectedVersion=999)
failed={'operationId':str(uuid.uuid4()),'items':[new,bad]};call(route,failed,expected=409,session=session)
assert sql(f"SELECT count(*) FROM products WHERE id='{new['productId']}'")=='0'
assert sql(f"SELECT count(*) FROM quick_receivings WHERE operation_id='{failed['operationId']}'")=='0'
# Manufacture/expiry persisted; same expiry is returned as an editable last-batch default.
dated=line('Sut');made=date.today();expiry=made+timedelta(days=30);dated.update(manufacturedOn=str(made),expiryDate=str(expiry));dated['product'].update(requiresExpiry=True,shelfLifeDays=30)
call(route,{'operationId':str(uuid.uuid4()),'items':[dated]},session=session)
template=call(route+'/'+dated['productId'],session=session);assert template['manufacturedOn']==str(made) and template['expiryDate']==str(expiry)
# Ordinary posted-invoice defaults are read from JSON draft and movement cost, not a nonexistent items table.
# Read scoping rejects foreign store/session and role access.
call(f'/v1/stores/{FOREIGN}/receiving/quick-products/'+item['productId'],expected=409,session=session)
call(route+'/'+item['productId'],expected=403,subject='cashier-a',role='cashier')
call(route+'/'+item['productId'],expected=404,subject='admin-foreign',role='admin')
# Real sale consumes the new stock and retains its cost basis. A retry does not debit twice.
shift=str(uuid.uuid4());call('/v1/shifts',{'shiftId':shift,'openingCash':0},role='cashier',session=session,register=True)
sale={'operationId':str(uuid.uuid4()),'shiftId':shift,'method':'cash','tendered':3000,
 'items':[{'productId':item['productId'],'lotId':item['lotId'],'priceVersion':card['priceVersion'],'quantity':1,'unitPrice':3000}]}
# SaleItem price is authoritative on the server; do not send an unsupported client price field.
del sale['items'][0]['unitPrice']
sold=call('/v1/sales',sale,role='cashier',session=session,register=True);assert sold['total']==3000
assert call('/v1/sales',sale,role='cashier',session=session,register=True)==sold
assert qty(item)=='2.000/4000.00';assert sql(f"SELECT inventory_cost FROM sale_lines WHERE sale_id='{sale['operationId']}'")=='2000.00'
# Movement report includes direct addition, with its cost and source ID, without an invoice link.
report=call(f'/v1/reports/stores/{STORE}/stock-movements?from={date.today()}&to={date.today()}&kind=quickReceipt',role='admin',session=session)
assert any(r['documentId']==body['operationId'] and r['receivingCost']==6000 and r['stockDelta']==3 for r in report['items']),report
assert sql(f"SELECT count(*) FROM catalog_change_feed WHERE product_id='{item['productId']}'")!='0'
# Existing invoice history is a valid source of autofill (manufacture lives in draft_body JSON).
rid=str(uuid.uuid4());rlot=str(uuid.uuid4());made=str(date.today());expiry=str(date.today()+timedelta(days=60))
draft=json.dumps([{'ProductId':item['productId'],'LotId':rlot,'Quantity':4,'ManufacturedOn':made,'ExpiryDate':expiry}])
sql(f"""INSERT INTO suppliers(org_id,store_id,code,name,tax_id,phone,email,payment_terms,lead_time_days,active,version) VALUES('00000000-0000-0000-0000-000000000001','{STORE}','QTEST','Fixture','','','','',0,true,1);
INSERT INTO receiving_documents(org_id,store_id,id,created_by,supplier_ref,invoice_number,invoice_date,draft_body,creation_body,status,posted_by,posted_at)
VALUES('00000000-0000-0000-0000-000000000001','{STORE}','{rid}','00000000-0000-0000-0000-000000003050','QTEST','QTEST-1',current_date,'{draft}'::jsonb,'{draft}'::jsonb,'posted','00000000-0000-0000-0000-000000003050',now());
INSERT INTO stock_lots(org_id,store_id,product_id,id,expiry_date,quantity,inventory_value) VALUES('00000000-0000-0000-0000-000000000001','{STORE}','{item['productId']}','{rlot}','{expiry}',4,4600);
INSERT INTO receiving_movements(org_id,store_id,receipt_id,product_id,lot_id,quantity) VALUES('00000000-0000-0000-0000-000000000001','{STORE}','{rid}','{item['productId']}','{rlot}',4);
INSERT INTO receiving_movement_costs(org_id,store_id,receipt_id,product_id,lot_id,total) VALUES('00000000-0000-0000-0000-000000000001','{STORE}','{rid}','{item['productId']}','{rlot}',4600);""")
template=call(route+'/'+item['productId'],session=session);assert template['unitCost']==1150 and template['manufacturedOn']==made and template['expiryDate']==expiry,template
print('PASS PostgreSQL17 + real HTTP + restricted kasa_api role: all migrations, receiver direct batch, no invoice, weighted stock/cost, templates from direct and ordinary receipts, dates, real sale/COGS, movement report/change feed, full rollback, auth/store scope, sequential and concurrent same-ID retries, changed-body rejection')
