import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kassa_mobile/main.dart';
import 'package:kassa_mobile/pos.dart';

const storage = FlutterSecureStorage();
const store = '30000000-0000-4000-8000-000000000001';
const checkout = '30000000-0000-4000-8000-000000000002';
const owner = '30000000-0000-4000-8000-000000000003';
const cashier = '30000000-0000-4000-8000-000000000004';
const teamRegister = '30000000-0000-4000-8000-000000000005';
const inactive = '30000000-0000-4000-8000-000000000006';
const shift = '30000000-0000-4000-8000-000000000007';

class PreviewLogin extends Login {
  PreviewLogin() : super(storage);
  @override
  String get subject => 'owner-preview';
  @override
  Future<void> signIn() async {}
  @override
  Future<String> accessToken({bool force = false}) async => 'preview-token';
}

http.Response reply(Object? value) => http.Response.bytes(
  utf8.encode(jsonEncode(value)), 200,
  headers: {'content-type': 'application/json; charset=utf-8'},
);

Map<String, Object> device(String id, String name, String staff, {bool active = true}) => {
  'registerId': id, 'name': name, 'active': active, 'version': 1,
  'cashierIds': [if (active) staff],
};

Future<void> loadFont(String family, String path) async {
  final loader = FontLoader(family)
    ..addFont(Future.value(ByteData.sublistView(File(path).readAsBytesSync())));
  await loader.load();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUpAll(() async {
    final font = Platform.environment['KASSA_PREVIEW_TEXT_FONT'];
    final icons = Platform.environment['KASSA_PREVIEW_ICONS'];
    if (font != null) await loadFont('Roboto', font);
    if (icons != null && icons.isNotEmpty) await loadFont('MaterialIcons', icons);
  });

  testWidgets('capture the shop, staff, register selector and sales screen', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(390, 780);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    FlutterSecureStorage.setMockInitialValues({});
    final session = Pos(
      storage,
      login: PreviewLogin(),
      productionContract: true,
      client: MockClient((request) async {
        switch (request.url.path) {
          case '/v1/me/stores':
            return reply({'items': [{
              'storeId': store, 'name': 'Korzinka', 'timeZone': 'Asia/Tashkent',
              'utcOffsetMinutes': 300, 'isOwner': true,
              'registers': [{'registerId': checkout, 'name': 'Касса 2'}],
            }], 'nextOffset': null});
          case '/v1/me/roles':
            return reply({
              'storeId': store, 'staffId': owner, 'owner': true,
              'roles': ['admin', 'cashier', 'receiver'],
            });
          case '/v1/stores/$store/receiving/catalog/proposals':
            return reply([]);
          case '/v1/stores/$store/staff':
            return reply([
              {'staffId': owner, 'subject': 'Владелец магазина', 'roles': ['owner'],
                'active': true, 'registerIds': [checkout]},
              {'staffId': cashier, 'subject': 'Дилшод Каримов', 'roles': ['cashier'],
                'active': true, 'registerIds': [teamRegister]},
            ]);
          case '/v1/stores/$store/registers':
            return reply([
              device(checkout, 'Касса 2', owner),
              device(teamRegister, 'Касса 3', cashier),
              device(inactive, 'Касса 1', owner, active: false),
            ]);
          case '/v1/shifts/active':
            return reply({'shift': {
              'shiftId': shift, 'businessDate': '2026-11-17',
              'expectedCash': 920000, 'manualCashlessSales': 244000,
            }});
          default:
            fail('Unexpected request ${request.url.path}');
        }
      }),
    );
    final key = KassaLocation(store, checkout).storageKey(
      session.api.origin.origin, 'owner-preview', Desk.cashier, 'register-key',
    );
    await storage.write(key: key, value: 'a' * 64);
    final boundary = GlobalKey();
    Future<void> tap(Finder finder) async {
      await tester.ensureVisible(finder);
      await tester.tap(finder);
      await tester.pumpAndSettle();
    }

    Future<void> capture(String name) async {
      await tester.runAsync(() async {
        final target = boundary.currentContext!.findRenderObject()! as RenderRepaintBoundary;
        final image = await target.toImage(pixelRatio: 2);
        try {
          final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
          final file = File('design-preview/$name.png');
          file.parent.createSync(recursive: true);
          file.writeAsBytesSync(bytes!.buffer.asUint8List());
        } finally {
          image.dispose();
        }
      });
    }

    await tester.pumpWidget(RepaintBoundary(key: boundary, child: KassaApp(pos: session)));
    await tester.pumpAndSettle();
    await tap(find.text('Войти в аккаунт'));
    await tap(find.text('Сотрудники и кассы'));
    expect(find.text('Дилшод Каримов'), findsOneWidget);
    await capture('01-staff');
    await tap(find.text('Кассы'));
    expect(find.text('Отключена'), findsOneWidget);
    await capture('02-registers');

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tap(find.byTooltip('Магазины и кассы'));
    await capture('03-stores');
    await tap(find.text('Korzinka'));
    expect(find.text('Назначенные кассы'), findsOneWidget);
    await capture('04-assigned');
    await tap(find.text('Касса 2'));
    expect(find.text('Смена открыта'), findsOneWidget);
    await capture('05-sales');
    expect(tester.takeException(), isNull);
  });
}
