import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kassa_mobile/main.dart';
import 'package:kassa_mobile/pos.dart';

class SignedInTestLogin extends Login {
  SignedInTestLogin(super.storage);

  @override
  Future<void> signIn() async {}

  @override
  Future<String> accessToken({bool force = false}) async => 'test-access-token';
}

Finder roleButton(String role) => find.widgetWithText(TextButton, role);
Finder roleItem(String role) =>
    find.widgetWithText(CheckedPopupMenuItem<Desk>, role);

void main() {
  const storage = FlutterSecureStorage();
  setUp(() => FlutterSecureStorage.setMockInitialValues({}));

  testWidgets(
    'small-screen pilot names the current demo role and switches roles',
    (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = const Size(360, 720);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);

      await tester.pumpWidget(const KassaApp());
      await tester.tap(find.text('Учебный режим').first);
      await tester.pumpAndSettle();
      expect(find.text('Смена закрыта'), findsOneWidget);

      await tester.tap(roleButton('Кассир'));
      await tester.pumpAndSettle();
      expect(
        tester.widget<CheckedPopupMenuItem<Desk>>(roleItem('Кассир')).checked,
        isTrue,
      );
      expect(roleItem('Администратор'), findsNothing);
      await tester.tap(roleItem('Приёмщик'));
      await tester.pumpAndSettle();
      expect(roleButton('Приёмщик'), findsOneWidget);
      expect(find.text('Добавить товар'), findsOneWidget);
      expect(find.byTooltip('Сканировать штрихкод'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('signed-in role menu only lists roles confirmed by the API', (
    tester,
  ) async {
    var allowAdmin = true;
    final pos = Pos(
      storage,
      login: SignedInTestLogin(storage),
      client: MockClient((request) async {
        expect(request.method, 'GET');
        expect(request.headers['Authorization'], 'Bearer test-access-token');
        expect(request.headers.containsKey('X-Register-Key'), isFalse);
        final path = request.url.path;
        expect(path, startsWith('/v1/stores/$testStore/'));
        if (path.endsWith('/registers') && !allowAdmin) {
          return http.Response('{"code":"staff_access_denied"}', 403);
        }
        expect(
          path,
          anyOf(
            endsWith('/registers'),
            endsWith('/receiving/catalog/proposals'),
          ),
        );
        return http.Response('[]', 200);
      }),
    );

    await tester.pumpWidget(KassaApp(pos: pos));
    await tester.tap(find.text('Войти'));
    await tester.pumpAndSettle();
    expect(roleButton('Администратор'), findsOneWidget);

    await tester.tap(roleButton('Администратор'));
    await tester.pumpAndSettle();
    expect(roleItem('Приёмщик'), findsOneWidget);
    expect(roleItem('Кассир'), findsNothing);
    expect(
      tester
          .widget<CheckedPopupMenuItem<Desk>>(roleItem('Администратор'))
          .checked,
      isTrue,
    );
    await tester.tap(roleItem('Приёмщик'));
    await tester.pumpAndSettle();
    expect(roleButton('Приёмщик'), findsOneWidget);

    allowAdmin = false;
    await tester.tap(roleButton('Приёмщик'));
    await tester.pumpAndSettle();
    expect(roleItem('Администратор'), findsNothing);
    expect(
      tester.widget<CheckedPopupMenuItem<Desk>>(roleItem('Приёмщик')).checked,
      isTrue,
    );
    expect(tester.takeException(), isNull);
  });
}
