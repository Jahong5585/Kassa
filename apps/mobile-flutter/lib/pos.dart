import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:flutter_appauth/flutter_appauth.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;

typedef Json = Map<String, dynamic>;

const apiOrigin = 'https://kassa.qr-space.uz';
const issuer = '$apiOrigin/auth/realms/kasa';
const clientId = 'kasa-flutter';
const redirectUri = 'uz.qrspace.kassa:/oauth2redirect';
const testStore = '20000000-0000-0000-0000-000000000011';
const testRegister = '20000000-0000-0000-0000-000000000021';
const _discovery = '$issuer/.well-known/openid-configuration';

String newId() {
  final random = Random.secure();
  final bytes = List<int>.generate(16, (_) => random.nextInt(256));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  final hex = bytes
      .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
      .join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
}

String money(num amount) =>
    '${amount.toStringAsFixed(amount % 1 == 0 ? 0 : 2)} сум';
num? parseAmount(String input) {
  final normalized = input.trim().replaceAll(',', '.');
  if (!RegExp(r'^\d{1,10}(\.\d{1,2})?$').hasMatch(normalized)) return null;
  return num.tryParse(normalized);
}

num? parseQuantity(String input) {
  final normalized = input.trim().replaceAll(',', '.');
  if (!RegExp(r'^\d{1,9}(\.\d{1,3})?$').hasMatch(normalized)) return null;
  return num.tryParse(normalized);
}

String dateText(DateTime date) => date.toIso8601String().substring(0, 10);
const _registerKeyName = 'kassa.test.register.key.v1';

class ApiFailure implements Exception {
  ApiFailure(this.status, this.code);
  final int status;
  final String code;
  bool get uncertain =>
      status == 0 || status == 408 || status == 429 || status >= 500;

  @override
  String toString() {
    final known = switch (code) {
      'register_access_denied' =>
        'Нет доступа к кассе. Проверьте аккаунт и ключ кассы.',
      'staff_access_denied' =>
        'Этот аккаунт не имеет доступа к выбранному разделу.',
      'admin_mfa_required' =>
        'Для входа администратора требуется второй фактор.',
      'register_key_required' => 'Введите ключ тестовой кассы.',
      'store_not_found' => 'У аккаунта нет доступа к тестовому магазину.',
      'product_not_found' => 'Товар не найден.',
      'insufficient_stock' => 'Недостаточно товара в партии.',
      'insufficient_cash_balance' => 'В смене недостаточно наличных.',
      'invalid_invoice_date' => 'Проверьте дату накладной.',
      'network' => 'Связь с сервером прервалась.',
      'sign_in_required' => 'Войдите заново для продолжения.',
      _ => null,
    };
    return known ?? 'Сервер отклонил запрос (HTTP $status, $code).';
  }
}

class Login {
  Login(this._storage);
  final FlutterSecureStorage _storage;
  final _appAuth = const FlutterAppAuth();
  String? _accessToken;
  String? _refreshToken;
  DateTime? _expiresAt;
  String? _subject;
  Future<String>? _refreshing;
  String get subject => _subject ?? (throw ApiFailure(401, 'sign_in_required'));

  Future<void> signIn() async {
    final tokens = await _appAuth.authorizeAndExchangeCode(
      AuthorizationTokenRequest(
        clientId,
        redirectUri,
        discoveryUrl: _discovery,
        scopes: const ['openid'],
        promptValues: const ['login'],
      ),
    );
    final access = tokens.accessToken;
    final idToken = tokens.idToken;
    if (access == null || idToken == null) {
      throw ApiFailure(401, 'sign_in_required');
    }
    final claims =
        jsonDecode(
              utf8.decode(
                base64Url.decode(base64Url.normalize(idToken.split('.')[1])),
              ),
            )
            as Json;
    final sub = claims['sub'];
    if (sub is! String || sub.isEmpty || claims['iss'] != issuer) {
      throw ApiFailure(401, 'sign_in_required');
    }
    _subject = sub;
    _accessToken = access;
    _refreshToken = tokens.refreshToken;
    _expiresAt = tokens.accessTokenExpirationDateTime;
  }

  Future<String> accessToken({bool force = false}) async {
    final expiry = _expiresAt;
    if (!force &&
        _accessToken != null &&
        (expiry == null ||
            expiry.isAfter(DateTime.now().add(const Duration(seconds: 20))))) {
      return _accessToken!;
    }
    final refreshing = _refreshing;
    if (refreshing != null) return refreshing;
    final request = _refresh();
    _refreshing = request;
    try {
      return await request;
    } finally {
      _refreshing = null;
    }
  }

  Future<String> _refresh() async {
    final refresh = _refreshToken;
    final current = _subject;
    if (refresh == null || current == null) {
      throw ApiFailure(401, 'sign_in_required');
    }
    try {
      final tokens = await _appAuth.token(
        TokenRequest(
          clientId,
          redirectUri,
          discoveryUrl: _discovery,
          refreshToken: refresh,
        ),
      );
      if (tokens.accessToken == null || current != _subject) {
        throw ApiFailure(401, 'sign_in_required');
      }
      _accessToken = tokens.accessToken;
      _expiresAt = tokens.accessTokenExpirationDateTime;
      _refreshToken = tokens.refreshToken ?? refresh;
      return _accessToken!;
    } on Exception {
      throw ApiFailure(401, 'sign_in_required');
    }
  }

  void signOut() {
    _accessToken = null;
    _refreshToken = null;
    _expiresAt = null;
    _subject = null;
  }

  Future<String?> readRegisterKey() => _storage.read(key: _registerKeyName);
  Future<void> setRegisterKey(String key) async {
    if (!RegExp(r'^[A-Fa-f0-9]{64}$').hasMatch(key)) {
      throw const FormatException('Ключ кассы должен состоять из 64 символов.');
    }
    await _storage.write(key: _registerKeyName, value: key);
  }
}

enum Desk {
  cashier('Кассир'),
  receiving('Приёмщик'),
  admin('Администратор');

  const Desk(this.roleName);
  final String roleName;
}

class ServerApi {
  ServerApi(this.login, {http.Client? client})
    : _client = client ?? http.Client();
  final Login login;
  final http.Client _client;
  String get store => '/v1/stores/$testStore';

  Future<dynamic> request(
    String method,
    String path, {
    bool cashier = false,
    Json? body,
    Map<String, String>? query,
  }) async {
    final url = Uri.parse('$apiOrigin$path').replace(queryParameters: query);
    final headers = <String, String>{'Accept': 'application/json'};
    if (cashier) {
      final key = await login.readRegisterKey();
      if (key == null) throw ApiFailure(403, 'register_key_required');
      headers.addAll({'X-Register-Id': testRegister, 'X-Register-Key': key});
    }
    if (body != null) headers['Content-Type'] = 'application/json';
    for (var attempt = 0; attempt < 2; attempt++) {
      headers['Authorization'] =
          'Bearer ${await login.accessToken(force: attempt > 0)}';
      final request = http.Request(method, url)
        ..followRedirects = false
        ..headers.addAll(headers);
      if (body != null) request.body = jsonEncode(body);
      final http.Response response;
      try {
        response = await _client
            .send(request)
            .then(http.Response.fromStream)
            .timeout(const Duration(seconds: 15));
      } on TimeoutException {
        throw ApiFailure(0, 'network');
      } on http.ClientException {
        throw ApiFailure(0, 'network');
      }
      if (response.statusCode == 401 && attempt == 0) continue;
      dynamic data;
      if (response.bodyBytes.isNotEmpty) {
        try {
          data = jsonDecode(utf8.decode(response.bodyBytes));
        } on FormatException {
          if (response.statusCode >= 200 && response.statusCode < 300) {
            throw ApiFailure(0, 'invalid_server_response');
          }
        }
      }
      if (response.statusCode >= 200 && response.statusCode < 300) return data;
      final code = data is Json ? data['code'] : null;
      throw ApiFailure(
        response.statusCode,
        code is String ? code : 'request_failed',
      );
    }
    throw ApiFailure(401, 'sign_in_required');
  }

  Future<Set<Desk>> allowedDesks() async {
    final checks = await Future.wait([
      _canOpen('/v1/shifts/active', cashier: true),
      _canOpen('$store/receiving/catalog/proposals'),
      _canOpen('$store/registers'),
    ]);
    return {
      if (checks[0]) Desk.cashier,
      if (checks[1]) Desk.receiving,
      if (checks[2]) Desk.admin,
    };
  }

  Future<bool> _canOpen(String path, {bool cashier = false}) async {
    try {
      await request('GET', path, cashier: cashier);
      return true;
    } on ApiFailure catch (failure) {
      if (failure.status == 403 || failure.status == 404) return false;
      rethrow;
    }
  }

  void close() => _client.close();
}

class CartLine {
  CartLine(this.product, this.lot, this.quantity);
  final Json product;
  final Json lot;
  num quantity;
  Json? get label => product['priceLabel'] as Json?;
  String get name => product['name'] as String;
  num get unitPrice =>
      (lot['markdownUnitPrice'] ?? product['unitPrice']) as num;
  num get estimated =>
      (label?['printedTotal'] as num?) ??
      ((quantity * unitPrice * 100).round() / 100);
  Json get item => {
    'productId': product['productId'],
    'lotId': lot['lotId'],
    'priceVersion': product['priceVersion'],
    'quantity': quantity,
    if (product['promotionVersion'] != null)
      'promotionVersion': product['promotionVersion'],
    if (lot['lotPriceVersion'] != null)
      'lotPriceVersion': lot['lotPriceVersion'],
    if (label != null) 'priceLabelId': label!['labelId'],
  };
}

class Pos {
  Pos(this.storage, {Login? login, http.Client? client}) {
    this.login = login ?? Login(storage);
    api = ServerApi(this.login, client: client);
  }

  final FlutterSecureStorage storage;
  late final Login login;
  late final ServerApi api;
  bool demo = false;
  String get _saleJournal => 'kassa.sale.v1.$testRegister.${login.subject}';
  String get _cashJournal => 'kassa.cash.v1.$testRegister.${login.subject}';
  String get _draftKey => demo
      ? 'kassa.demo.draft.v1'
      : 'kassa.draft.v1.$testStore.${login.subject}';
  static const _demoKey = 'kassa.demo.v1';
  Json _local = {'products': <Json>[], 'shift': null};

  Future<void> loadDemo() async {
    final raw = await storage.read(key: _demoKey);
    if (raw != null) _local = jsonDecode(raw) as Json;
    demo = true;
  }

  Future<void> _saveDemo(Json updated) async {
    await storage.write(key: _demoKey, value: jsonEncode(updated));
    _local = updated;
  }

  List<Json> get _products => (_local['products'] as List).cast<Json>();
  Json? get _shift => _local['shift'] as Json?;
  Future<Json?> activeShift() async => demo
      ? _shift
      : (await api.request('GET', '/v1/shifts/active', cashier: true)) as Json?;

  Future<Json> openShift(num amount) async {
    final id = newId();
    if (!demo) {
      return await api.request(
            'POST',
            '/v1/shifts',
            cashier: true,
            body: {'shiftId': id, 'openingCash': amount},
          )
          as Json;
    }
    final shift = <String, dynamic>{
      'shiftId': id,
      'openingCash': amount,
      'expectedCash': amount,
      'cashSales': 0,
      'manualCashlessSales': 0,
      'cashIn': 0,
      'cashOut': 0,
      'salesCount': 0,
    };
    await _saveDemo({..._local, 'shift': shift});
    return shift;
  }

  Future<void> closeShift(Json shift, num counted) async {
    if (!demo) {
      await api.request(
        'POST',
        '/v1/shifts/${shift['shiftId']}/close',
        cashier: true,
        body: {'countedCash': counted},
      );
      return;
    }
    await _saveDemo({..._local, 'shift': null});
  }

  Future<Json> cashierProduct(String barcode) async {
    if (!demo) {
      return await api.request(
            'GET',
            '/v1/catalog/barcodes/${Uri.encodeComponent(barcode)}',
            cashier: true,
          )
          as Json;
    }
    for (final product in _products) {
      if (product['barcode'] == barcode) return product;
    }
    throw ApiFailure(404, 'product_not_found');
  }

  Future<Json> receivingProduct(String barcode) async {
    if (demo) return cashierProduct(barcode);
    return await api.request(
          'GET',
          '${api.store}/receiving/catalog/barcodes/${Uri.encodeComponent(barcode)}',
        )
        as Json;
  }

  Future<void> addProduct(
    String name,
    String barcode,
    bool expiry,
    num? price,
    num? stock,
  ) async {
    if (!demo) {
      await api.request(
        'POST',
        '${api.store}/receiving/catalog/proposals',
        body: {
          'proposalId': newId(),
          'product': {
            'name': name,
            'unit': 'piece',
            'requiresExpiry': expiry,
            'barcodes': [
              {'code': barcode, 'quantityFactor': 1},
            ],
          },
        },
      );
      return;
    }
    if (_products.any((product) => product['barcode'] == barcode)) {
      throw const FormatException('Товар с таким штрихкодом уже есть.');
    }
    final product = {
      'productId': newId(),
      'barcode': barcode,
      'name': name,
      'unit': 'piece',
      'priceVersion': 1,
      'unitPrice': price,
      'requiresExpiry': expiry,
      'lots': [
        {'lotId': newId(), 'quantity': stock},
      ],
    };
    await _saveDemo({
      ..._local,
      'products': [..._products, product],
    });
  }

  Future<num> quote(String shiftId, List<CartLine> lines) async {
    if (demo) return lines.fold<num>(0, (sum, line) => sum + line.estimated);
    final result =
        await api.request(
              'POST',
              '/v1/shifts/$shiftId/quote',
              cashier: true,
              body: {'items': lines.map((line) => line.item).toList()},
            )
            as Json;
    return result['total'] as num;
  }

  Future<Json?> pendingSale() =>
      demo ? Future.value(null) : _read(_saleJournal);
  Future<Json?> pendingMovement() =>
      demo ? Future.value(null) : _read(_cashJournal);

  Future<Json> sell(
    String shiftId,
    List<CartLine> lines,
    num total,
    num? tendered,
  ) async {
    final body = <String, dynamic>{
      'operationId': newId(),
      'shiftId': shiftId,
      'method': tendered == null ? 'manualCashless' : 'cash',
      'tendered': tendered,
      'items': lines.map((line) => line.item).toList(),
    };
    if (!demo) {
      await storage.write(key: _saleJournal, value: jsonEncode(body));
      return _submitSale(body, reconcile: false);
    }
    final shift = _shift!;
    final products = _products.map((product) {
      final lots = (product['lots'] as List).cast<Json>().map((lot) {
        final sold = lines
            .where((line) => line.lot['lotId'] == lot['lotId'])
            .fold<num>(0, (sum, line) => sum + line.quantity);
        final remaining = (lot['quantity'] as num) - sold;
        if (remaining < 0) throw ApiFailure(409, 'insufficient_stock');
        return {...lot, 'quantity': remaining};
      }).toList();
      return {...product, 'lots': lots};
    }).toList();
    final updated = {
      ...shift,
      'expectedCash':
          (shift['expectedCash'] as num) + (tendered == null ? 0 : total),
      'cashSales': (shift['cashSales'] as num) + (tendered == null ? 0 : total),
      'manualCashlessSales':
          (shift['manualCashlessSales'] as num) +
          (tendered == null ? total : 0),
      'salesCount': (shift['salesCount'] as int) + 1,
    };
    await _saveDemo({..._local, 'shift': updated, 'products': products});
    return {
      'operationId': body['operationId'],
      'method': body['method'],
      'total': total,
      'tendered': tendered,
      'changeDue': tendered == null ? null : tendered - total,
      'receiptMark': 'Тестовый • нефискальный',
      'lines': lines
          .map(
            (line) => {
              'name': line.name,
              'quantity': line.quantity,
              'lineTotal': line.estimated,
            },
          )
          .toList(),
    };
  }

  Future<Json> recoverSale(Json body) => _submitSale(body, reconcile: true);

  Future<Json> _submitSale(Json body, {required bool reconcile}) async {
    final key = _saleJournal;
    if (reconcile) {
      try {
        final sale =
            await api.request(
                  'GET',
                  '/v1/sales/${body['operationId']}',
                  cashier: true,
                )
                as Json;
        await storage.delete(key: key);
        return sale;
      } on ApiFailure catch (error) {
        if (error.status != 404) rethrow;
      }
    }
    try {
      final sale =
          await api.request('POST', '/v1/sales', cashier: true, body: body)
              as Json;
      await storage.delete(key: key);
      return sale;
    } on ApiFailure catch (error) {
      if (!error.uncertain && error.status != 401 && error.status != 403) {
        await storage.delete(key: key);
      }
      rethrow;
    }
  }

  Future<void> moveCash(
    String shiftId,
    String direction,
    num amount,
    String reason,
  ) async {
    final body = {
      'operationId': newId(),
      'direction': direction,
      'amount': amount,
      'reason': reason,
    };
    if (!demo) {
      await storage.write(
        key: _cashJournal,
        value: jsonEncode({'shiftId': shiftId, 'body': body}),
      );
      await recoverMovement({'shiftId': shiftId, 'body': body});
      return;
    }
    final shift = _shift!;
    final expected =
        (shift['expectedCash'] as num) + (direction == 'in' ? amount : -amount);
    if (expected < 0) throw ApiFailure(409, 'insufficient_cash_balance');
    final field = direction == 'in' ? 'cashIn' : 'cashOut';
    await _saveDemo({
      ..._local,
      'shift': {
        ...shift,
        'expectedCash': expected,
        field: (shift[field] as num) + amount,
      },
    });
  }

  Future<void> recoverMovement(Json pending) async {
    final key = _cashJournal;
    try {
      await api.request(
        'POST',
        '/v1/shifts/${pending['shiftId']}/cash-movements',
        cashier: true,
        body: pending['body'] as Json,
      );
      await storage.delete(key: key);
    } on ApiFailure catch (error) {
      if (!error.uncertain && error.status != 401 && error.status != 403) {
        await storage.delete(key: key);
      }
      rethrow;
    }
  }

  Future<Json?> readDraft() => _read(_draftKey);
  Future<void> saveDraft(Json draft) =>
      storage.write(key: _draftKey, value: jsonEncode(draft));
  Future<Json?> _read(String key) async {
    final value = await storage.read(key: key);
    return value == null ? null : jsonDecode(value) as Json;
  }
}
