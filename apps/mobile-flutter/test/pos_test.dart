import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kassa_mobile/pos.dart';

class TestLogin extends Login {
  TestLogin(super.storage, this.account);

  final String account;

  @override
  String get subject => account;

  @override
  Future<String> accessToken({bool force = false}) async => 'test-access-token';

  @override
  Future<String?> readRegisterKey() async => List.filled(64, 'a').join();
}

void main() {
  const storage = FlutterSecureStorage();
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  test('cashier role is confirmed even when no shift is open', () async {
    final pos = Pos(
      storage,
      login: TestLogin(storage, 'cashier'),
      client: MockClient((request) async {
        expect(request.method, 'GET');
        if (request.url.path == '/v1/shifts/active') {
          expect(request.headers['X-Register-Id'], testRegister);
          expect(request.headers.containsKey('X-Register-Key'), isTrue);
          return http.Response('', 204);
        }
        expect(request.headers.containsKey('X-Register-Key'), isFalse);
        return http.Response('{"code":"staff_access_denied"}', 403);
      }),
    );
    addTearDown(pos.api.close);
    expect(await pos.api.allowedDesks(), {Desk.cashier});
  });

  test('demo product sells for cashless payment without adding cash', () async {
    final pos = Pos(storage);
    addTearDown(pos.api.close);
    await pos.loadDemo();
    await pos.addProduct('Чай', '4780012345678', false, 5000, 3);
    final product = await pos.cashierProduct('4780012345678');
    final shift = await pos.openShift(1000);
    final lines = [
      CartLine(product, (product['lots'] as List).first as Json, 2),
    ];
    final total = await pos.quote(shift['shiftId'] as String, lines);
    final receipt = await pos.sell(
      shift['shiftId'] as String,
      lines,
      total,
      null,
    );
    expect(receipt['total'], 10000);
    expect(receipt['method'], 'manualCashless');
    expect((await pos.activeShift())?['expectedCash'], 1000);
    expect((await pos.activeShift())?['manualCashlessSales'], 10000);
    expect(
      ((await pos.cashierProduct('4780012345678'))['lots'] as List)
          .first['quantity'],
      1,
    );
  });

  for (final committed in [true, false]) {
    test(
      'lost sale response retains original operation, committed=$committed',
      () async {
        Json? stored;
        Json? initial;
        var writes = 0;
        final client = MockClient((request) async {
          expect(request.url.host, 'kassa.qr-space.uz');
          expect(request.headers['X-Register-Id'], testRegister);
          if (request.method == 'GET') {
            expect(request.url.path, '/v1/sales/${initial?['operationId']}');
            return stored == null
                ? http.Response('{}', 404)
                : http.Response(jsonEncode(stored), 200);
          }
          final body = jsonDecode(request.body) as Json;
          if (initial == null) {
            initial = body;
            if (committed) {
              stored = body;
              writes++;
            }
            throw http.ClientException('Response lost');
          }
          expect(body, initial);
          stored = body;
          writes++;
          return http.Response(jsonEncode(stored), 200);
        });
        final pos = Pos(
          storage,
          login: TestLogin(storage, 'cashier-a'),
          client: client,
        );
        final other = Pos(storage, login: TestLogin(storage, 'cashier-b'));
        addTearDown(pos.api.close);
        addTearDown(other.api.close);

        await expectLater(
          pos.sell('shift', [], 100, 100),
          throwsA(isA<ApiFailure>()),
        );
        final pending = await pos.pendingSale();
        expect(pending, initial);
        expect(await other.pendingSale(), isNull);
        expect(await pos.recoverSale(pending!), initial);
        expect(await pos.pendingSale(), isNull);
        expect(writes, 1);
      },
    );
  }

  test(
    'cash movement retry keeps original ID and draft is isolated by account',
    () async {
      Json? initial;
      final pos = Pos(
        storage,
        login: TestLogin(storage, 'worker-a'),
        client: MockClient((request) async {
          expect(request.url.path, '/v1/shifts/shift/cash-movements');
          final body = jsonDecode(request.body) as Json;
          if (initial == null) {
            initial = body;
            return http.Response('{}', 503);
          }
          expect(body, initial);
          return http.Response('{}', 200);
        }),
      );
      final other = Pos(storage, login: TestLogin(storage, 'worker-b'));
      addTearDown(pos.api.close);
      addTearDown(other.api.close);

      await expectLater(
        pos.moveCash('shift', 'in', 5000, 'Размен'),
        throwsA(isA<ApiFailure>()),
      );
      await pos.recoverMovement((await pos.pendingMovement())!);
      expect(await pos.pendingMovement(), isNull);
      final draft = {
        'invoice': '01',
        'lines': [
          {'lotId': 'lot', 'quantity': 5},
        ],
      };
      await pos.saveDraft(draft);
      expect(await other.readDraft(), isNull);
      expect(await pos.readDraft(), draft);
    },
  );
}
