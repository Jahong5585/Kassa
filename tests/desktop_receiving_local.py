"""Disposable PostgreSQL 17 + API + desktop UI integration; never connects to production.

PG_BIN=/path/to/pgsql/bin DOTNET=/path/to/dotnet DESKTOP_SOURCE=/path/to/desktop/source
PLAYWRIGHT_MODULE=/path/to/playwright CHROMIUM_PATH=/path/to/chromium python3 tests/desktop_receiving_local.py
"""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
PG = Path(os.environ['PG_BIN'])
DOTNET = os.environ.get('DOTNET', 'dotnet')
DESKTOP = Path(os.environ['DESKTOP_SOURCE'])

def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]

with tempfile.TemporaryDirectory(prefix='kassa-receiving-') as temporary:
    folder = Path(temporary)
    port, api_port = free_port(), free_port()
    env = os.environ.copy()
    env.update(PGHOST='127.0.0.1', PGPORT=str(port), PGUSER=os.environ.get('USER', 'agent'))
    db_log = open(folder / 'postgres.log', 'w+')
    api_log = open(folder / 'api.log', 'w+')
    pg, api = None, None
    def sql(text, database='kasa'):
        result = subprocess.run([str(PG/'psql'), '-X', '-d', database, '-v', 'ON_ERROR_STOP=1'],
            input=text, text=True, env=env, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout
    try:
        subprocess.run([str(PG/'initdb'), '-D', str(folder/'data'), '--auth=trust', '--no-locale', '-E', 'UTF8'],
            env=env, check=True, stdout=subprocess.DEVNULL)
        pg = subprocess.Popen([str(PG/'postgres'), '-D', str(folder/'data'), '-h', '127.0.0.1', '-p', str(port),
            '-c', 'unix_socket_directories='], env=env, stdout=db_log, stderr=subprocess.STDOUT)
        for _ in range(100):
            if subprocess.run([str(PG/'pg_isready')], env=env, stdout=subprocess.DEVNULL,
                              stderr=subprocess.DEVNULL).returncode == 0:
                break
            time.sleep(0.05)
        sql('CREATE DATABASE kasa;', 'postgres')
        sql('CREATE ROLE kasa_owner; CREATE ROLE kasa_api LOGIN;')
        migrations = sorted((ROOT/'db').glob('[0-9]*.sql'))
        for migration in migrations:
            sql(migration.read_text())
        sql('''GRANT USAGE ON SCHEMA public TO kasa_api;
            GRANT SELECT ON registers,stores,staff,staff_stores,cashier_registers,products,store_prices,stock_lots,shifts,sales,sale_lines TO kasa_api;
            GRANT INSERT ON shifts,sales,sale_lines,audit_events TO kasa_api;
            GRANT UPDATE (closed_at,counted_cash) ON shifts TO kasa_api;
            GRANT UPDATE (quantity) ON stock_lots TO kasa_api;
            GRANT USAGE ON SEQUENCE audit_events_id_seq TO kasa_api;''')
        env['POS_TEST_REGISTER_KEY'] = secrets.token_hex(32)
        sql((ROOT/'tests/fixtures.sql').read_text())
        sql('''INSERT INTO suppliers VALUES ('00000000-0000-0000-0000-000000000001',
            '00000000-0000-0000-0000-000000000011','DEMO','Тестовый поставщик','','','','',0,true,1);
            INSERT INTO products (org_id,id,name,unit,requires_expiry,barcode,brand,category,shelf_life_days)
            VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000009999',
            'Pepsi 0.5l','piece',true,'4780022620139','Pepsi','Napitki',180);
            INSERT INTO product_barcodes VALUES ('00000000-0000-0000-0000-000000000002',
            '4780022620139','00000000-0000-0000-0000-000000009999',1);
            INSERT INTO store_prices (org_id,store_id,product_id,unit_price,version) VALUES
            ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000012',
            '00000000-0000-0000-0000-000000009999',12345,1);''')
        # A transport fixture, deliberately NOT an executable trusted by the Windows updater.
        updates = folder/'updates'
        updates.mkdir()
        payload = b'MZ-not-a-signed-installer'
        (updates/'KassaSetup-0.8.99.exe').write_bytes(payload)
        (updates/'latest.json').write_text(json.dumps(dict(schemaVersion=1, channel='stable', version='0.8.99',
            file='KassaSetup-0.8.99.exe', sha256=hashlib.sha256(payload).hexdigest(), bytes=len(payload),
            signerThumbprint='A'*40, publishedAtUtc='2026-11-20T13:00:00.000Z',
            releaseNotes='Локальный транспортный тест')))
        key = secrets.token_hex(32)
        env.update(ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=f'http://127.0.0.1:{api_port}',
            Auth__DevelopmentKey=key, Auth__Authority='kasa-local', Auth__Audience='kasa-ui',
            Auth__AdminAcr='test-mfa',
            ConnectionStrings__Pos=f'Host=127.0.0.1;Port={port};Database=kasa;Username=kasa_api',
            DesktopUpdates__Root=str(updates), RateLimiting__PermitLimit='2000')
        api = subprocess.Popen([DOTNET, str(ROOT/'src/Kasa.Api/bin/Release/net10.0/Kasa.Api.dll')], env=env,
            stdout=api_log, stderr=subprocess.STDOUT)
        for _ in range(100):
            if api.poll() is not None: raise RuntimeError('Local API failed to start')
            try:
                urllib.request.urlopen(f'http://127.0.0.1:{api_port}/health/live', timeout=0.2)
                break
            except Exception: time.sleep(0.1)
        def encode(value): return base64.urlsafe_b64encode(value).rstrip(b'=')
        header = encode(b'{"alg":"HS256","typ":"JWT"}')
        claims = encode(json.dumps(dict(iss='kasa-local', aud='kasa-ui', sub='admin-a',
            iat=int(time.time()), exp=int(time.time())+600, acr='test-mfa')).encode())
        message = header+b'.'+claims
        env['POS_TEST_UI_TOKEN'] = (message+b'.'+encode(hmac.digest(key.encode(), message, hashlib.sha256))).decode()
        env['POS_TEST_URL'] = f'http://127.0.0.1:{api_port}'
        subprocess.run(['node', str(DESKTOP/'tests/receiving-server.mjs')], env=env, check=True)
        count = sql("SELECT count(*) FROM receiving_product_creations;").splitlines()
        assert any(row.strip() == '2' for row in count), count
        prices = sql("SELECT unit_price FROM store_prices WHERE store_id = "
                     "'00000000-0000-0000-0000-000000000011' AND product_id IN "
                     "(SELECT request_id FROM receiving_product_creations);")
        assert '8000.00' in prices and '12345.00' not in prices, prices
        # Roll back only the additive receiving migration in the disposable database, then reapply.
        sql((ROOT/'db/rollback/041_receiving_catalog.down.sql').read_text())
        sql((ROOT/'db/041_receiving_catalog.sql').read_text())
        print('PASS: PostgreSQL 17 migrations, actual runtime grants, creation journal, 041 rollback/reapply')
    except Exception:
        api_log.flush()
        print((folder/'api.log').read_text()[-10000:])
        raise
    finally:
        for process in (api, pg):
            if process is not None:
                process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        api_log.close(); db_log.close()
