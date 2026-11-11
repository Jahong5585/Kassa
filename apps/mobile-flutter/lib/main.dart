import 'package:flutter/material.dart';
import 'package:flutter_appauth/flutter_appauth.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import 'pos.dart';

void main() => runApp(const KassaApp());

const _blue = Color(0xff304acb);
const _navy = Color(0xff15223a);
const _muted = Color(0xff475970);

extension on Desk {
  String get label => switch (this) {
    Desk.cashier => 'Касса',
    Desk.receiving => 'Приёмка',
    Desk.admin => roleName,
  };

  IconData get icon => switch (this) {
    Desk.cashier => Icons.point_of_sale_rounded,
    Desk.receiving => Icons.inventory_2_outlined,
    Desk.admin => Icons.admin_panel_settings_outlined,
  };
}

class KassaApp extends StatefulWidget {
  const KassaApp({this.pos, super.key});

  final Pos? pos;

  @override
  State<KassaApp> createState() => _KassaAppState();
}

class _KassaAppState extends State<KassaApp> {
  late final pos =
      widget.pos ??
      Pos(
        const FlutterSecureStorage(
          aOptions: AndroidOptions(encryptedSharedPreferences: true),
        ),
      );
  final roleButton = GlobalKey();
  static const demoDesks = {Desk.cashier, Desk.receiving};

  Desk? desk;
  Set<Desk> allowed = {};
  bool signedIn = false;
  bool busy = false;
  String? error;

  Future<void> run(Future<void> Function() operation) async {
    if (busy) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await operation();
    } on FlutterAppAuthUserCancelledException {
      // The system browser was dismissed.
    } on ApiFailure catch (failure) {
      if (mounted) {
        setState(() {
          error = failure.toString();
          if (failure.status == 401) clearSession();
        });
      }
    } on FormatException catch (failure) {
      if (mounted) setState(() => error = failure.message);
    } on Exception {
      if (mounted) {
        setState(() => error = 'Операция не выполнена. Повторите попытку.');
      }
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> signIn() => run(() async {
    pos.demo = false;
    await pos.login.signIn();
    if (!mounted) return;
    setState(() => signedIn = true);
    await loadDesks();
  });

  Future<void> openDemo(Desk selected) => run(() async {
    await pos.loadDemo();
    if (!mounted) return;
    setState(() {
      allowed = demoDesks;
      desk = selected;
    });
  });

  Future<void> loadDesks() async {
    final verified = await pos.api.allowedDesks();
    if (!mounted) return;
    setState(() {
      allowed = verified;
      if (!allowed.contains(desk)) {
        desk = allowed.contains(Desk.admin) ? Desk.admin : allowed.firstOrNull;
      }
    });
  }

  Future<void> configureRegister(BuildContext dialogContext) => run(() async {
    final key = await inputValue(
      dialogContext,
      'Подключить тестовую кассу',
      'Ключ кассы',
      secret: true,
    );
    if (key == null || !mounted) return;
    await pos.login.setRegisterKey(key);
    await loadDesks();
    if (mounted && desk == null) {
      setState(() => error = 'Сервер не подтвердил доступ к тестовой кассе.');
    }
  });

  Future<void> showRoles() async {
    if (busy) return;
    if (!pos.demo) {
      var checked = false;
      await run(() async {
        await loadDesks();
        checked = true;
      });
      if (!checked || !mounted || desk == null) return;
    }
    final buttonContext = roleButton.currentContext;
    if (buttonContext == null || !buttonContext.mounted) return;
    final button = buttonContext.findRenderObject()! as RenderBox;
    final overlay =
        Overlay.of(buttonContext).context.findRenderObject()! as RenderBox;
    final anchor = button.localToGlobal(
      Offset(0, button.size.height),
      ancestor: overlay,
    );
    final selected = await showMenu<Desk>(
      context: buttonContext,
      position: RelativeRect.fromRect(
        Rect.fromLTWH(anchor.dx, anchor.dy, button.size.width, 0),
        Offset.zero & overlay.size,
      ),
      items: [
        for (final role in Desk.values)
          if (allowed.contains(role))
            CheckedPopupMenuItem(
              value: role,
              checked: desk == role,
              child: Text(role.roleName),
            ),
      ],
    );
    if (mounted && selected != null && allowed.contains(selected)) {
      setState(() => desk = selected);
    }
  }

  void clearSession() {
    pos.login.signOut();
    pos.demo = false;
    signedIn = false;
    allowed = {};
    desk = null;
  }

  void leave() => setState(() {
    clearSession();
    error = null;
  });

  @override
  void dispose() {
    pos.api.close();
    super.dispose();
  }

  Widget welcome(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text('Рабочее место', style: Theme.of(context).textTheme.headlineSmall),
      const SizedBox(height: 6),
      const Text(
        'Тестовый магазин · kassa.qr-space.uz',
        style: TextStyle(color: _muted),
      ),
      const SizedBox(height: 16),
      if (signedIn)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (!busy && error == null)
                const Text(
                  'Сервер пока не подтвердил доступ к рабочим местам.',
                ),
              FilledButton(
                onPressed: busy ? null : () => run(loadDesks),
                child: const Text('Проверить доступ'),
              ),
              OutlinedButton(
                onPressed: busy ? null : () => configureRegister(context),
                child: const Text('Ключ кассы'),
              ),
            ],
          ),
        )
      else ...[
        FilledButton.icon(
          onPressed: busy ? null : signIn,
          icon: const Icon(Icons.login),
          label: const Text('Войти'),
        ),
        const SizedBox(height: 12),
        for (final role in demoDesks)
          Section(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(role.icon, color: _blue),
                  title: Text(role.label),
                  subtitle: Text(
                    role == Desk.cashier
                        ? 'Смена, штрихкоды и продажи'
                        : 'Карточки товаров и черновики накладных',
                  ),
                ),
                TextButton(
                  onPressed: busy ? null : () => openDemo(role),
                  child: const Text('Учебный режим'),
                ),
              ],
            ),
          ),
      ],
    ],
  );

  Widget workplace() {
    final roles = Desk.values.where(allowed.contains).toList();
    return IndexedStack(
      index: roles.indexOf(desk!),
      sizing: StackFit.expand,
      children: [
        for (final role in roles)
          ExcludeFocus(
            key: ValueKey(role),
            excluding: role != desk,
            child: switch (role) {
              Desk.cashier => CashierScreen(pos),
              Desk.receiving => ReceivingScreen(pos),
              Desk.admin => AdminScreen(pos),
            },
          ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Касса',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: _blue, primary: _blue),
        scaffoldBackgroundColor: const Color(0xfff3f6fa),
        appBarTheme: const AppBarTheme(
          backgroundColor: _navy,
          foregroundColor: Colors.white,
        ),
        inputDecorationTheme: const InputDecorationTheme(
          filled: true,
          fillColor: Colors.white,
          border: OutlineInputBorder(),
        ),
        useMaterial3: true,
      ),
      home: Builder(
        builder: (context) => Scaffold(
          appBar: AppBar(
            title: const Text('Касса'),
            actions: [
              if (signedIn || desk != null)
                IconButton(
                  tooltip: 'Выйти',
                  onPressed: busy ? null : leave,
                  icon: const Icon(Icons.logout),
                ),
              if (desk case final active?)
                TextButton(
                  key: roleButton,
                  style: TextButton.styleFrom(
                    foregroundColor: Colors.white,
                    disabledForegroundColor: Colors.white70,
                  ),
                  onPressed: busy ? null : showRoles,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(active.roleName),
                      const Icon(Icons.arrow_drop_down, size: 20),
                    ],
                  ),
                ),
            ],
          ),
          body: SafeArea(
            child: Column(
              children: [
                if (busy) const LinearProgressIndicator(),
                if (error case final message?)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                    child: StatusBox(message, error: true),
                  ),
                Expanded(child: desk == null ? welcome(context) : workplace()),
              ],
            ),
          ),
          bottomNavigationBar: pos.demo && desk != null
              ? NavigationBar(
                  selectedIndex: desk == Desk.cashier ? 0 : 1,
                  onDestinationSelected: (index) => setState(
                    () => desk = index == 0 ? Desk.cashier : Desk.receiving,
                  ),
                  destinations: [
                    for (final role in demoDesks)
                      NavigationDestination(
                        icon: Icon(role.icon),
                        label: role.label,
                      ),
                  ],
                )
              : null,
        ),
      ),
    );
  }
}

class Section extends StatelessWidget {
  const Section({required this.child, super.key});
  final Widget child;

  @override
  Widget build(BuildContext context) => Card(
    color: Colors.white,
    elevation: 0,
    margin: const EdgeInsets.only(bottom: 12),
    shape: RoundedRectangleBorder(
      borderRadius: BorderRadius.circular(14),
      side: const BorderSide(color: Color(0xffdbe3ee)),
    ),
    child: Padding(padding: const EdgeInsets.all(14), child: child),
  );
}

class StatusBox extends StatelessWidget {
  const StatusBox(this.message, {this.error = false, super.key});
  final String message;
  final bool error;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 12),
    child: DecoratedBox(
      decoration: BoxDecoration(
        color: error ? const Color(0xffffe9ee) : const Color(0xffeef1ff),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Padding(padding: const EdgeInsets.all(12), child: Text(message)),
    ),
  );
}

mixin Tasks<T extends StatefulWidget> on State<T> {
  bool busy = false;
  String? status;
  bool failed = false;

  Future<void> task(Future<void> Function() action) async {
    if (busy || !mounted) return;
    setState(() {
      busy = true;
      status = null;
      failed = false;
    });
    try {
      await action();
    } on ApiFailure catch (failure) {
      status = failure.toString();
      failed = true;
    } on FormatException catch (failure) {
      status = failure.message;
      failed = true;
    } on Exception {
      status = 'Операцию не удалось завершить. Повторите проверку.';
      failed = true;
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }
}

Future<String?> scanCode(BuildContext context) => Navigator.of(
  context,
).push<String>(MaterialPageRoute(builder: (_) => const ScannerScreen()));

class ScannerScreen extends StatefulWidget {
  const ScannerScreen({super.key});

  @override
  State<ScannerScreen> createState() => _ScannerScreenState();
}

class _ScannerScreenState extends State<ScannerScreen> {
  final camera = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
  );
  bool done = false;

  @override
  void dispose() {
    camera.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Сканировать штрихкод')),
    backgroundColor: Colors.black,
    body: Stack(
      alignment: Alignment.center,
      fit: StackFit.expand,
      children: [
        MobileScanner(
          controller: camera,
          onDetect: (capture) {
            if (done || !mounted) return;
            for (final barcode in capture.barcodes) {
              final code = barcode.rawValue?.trim();
              if (code != null && code.isNotEmpty && code.length <= 64) {
                done = true;
                Navigator.pop(context, code);
                break;
              }
            }
          },
          errorBuilder: (context, error) => const Center(
            child: Padding(
              padding: EdgeInsets.all(22),
              child: Text(
                'Камера недоступна. Проверьте разрешение или вернитесь к ручному вводу.',
                textAlign: TextAlign.center,
                style: TextStyle(color: Colors.white),
              ),
            ),
          ),
        ),
        IgnorePointer(
          child: Center(
            child: Container(
              width: 240,
              height: 115,
              decoration: BoxDecoration(
                border: Border.all(color: Colors.white, width: 2),
                borderRadius: BorderRadius.circular(12),
              ),
            ),
          ),
        ),
      ],
    ),
  );
}

Future<String?> inputValue(
  BuildContext context,
  String title,
  String label, {
  bool secret = false,
  bool numeric = false,
  String? initial,
}) async {
  var value = initial ?? '';
  return showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: TextFormField(
        initialValue: initial,
        autofocus: true,
        obscureText: secret,
        enableSuggestions: !secret,
        autocorrect: !secret,
        keyboardType: numeric
            ? const TextInputType.numberWithOptions(decimal: true)
            : TextInputType.text,
        decoration: InputDecoration(labelText: label),
        onChanged: (text) => value = text,
        onFieldSubmitted: (_) => Navigator.pop(context, value.trim()),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Отмена'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, value.trim()),
          child: const Text('Подтвердить'),
        ),
      ],
    ),
  );
}

class CashierScreen extends StatefulWidget {
  const CashierScreen(this.pos, {super.key});
  final Pos pos;
  @override
  State<CashierScreen> createState() => _CashierScreenState();
}

class _CashierScreenState extends State<CashierScreen>
    with Tasks<CashierScreen> {
  Pos get pos => widget.pos;
  final barcode = TextEditingController();
  final codeFocus = FocusNode();
  final lines = <CartLine>[];
  Json? shift;
  Json? receipt;
  Json? pending;
  Json? pendingMovement;
  bool ready = false;

  String get shiftId => shift!['shiftId'] as String;
  bool get locked => pending != null || pendingMovement != null;

  @override
  void initState() {
    super.initState();
    task(reload);
  }

  Future<void> reload() async {
    pending = await pos.pendingSale();
    pendingMovement = await pos.pendingMovement();
    shift = await pos.activeShift();
    ready = true;
  }

  @override
  void dispose() {
    barcode.dispose();
    codeFocus.dispose();
    super.dispose();
  }

  Future<void> configure() => task(() async {
    final key = await inputValue(
      context,
      'Подключить тестовую кассу',
      'Ключ кассы',
      secret: true,
    );
    if (key == null) return;
    ready = false;
    await pos.login.setRegisterKey(key);
    await reload();
    status = 'Тестовая касса подключена.';
  });

  Future<void> changeShift() => task(() async {
    final opened = shift != null;
    final value = await inputValue(
      context,
      opened ? 'Закрыть смену' : 'Открыть смену',
      opened ? 'Фактические наличные' : 'Наличные на начало смены',
      numeric: true,
      initial: opened ? null : '0',
    );
    if (value == null) return;
    final amount = parseAmount(value);
    if (amount == null) throw const FormatException('Укажите сумму наличных.');
    if (opened) {
      await pos.closeShift(shift!, amount);
      lines.clear();
    } else {
      await pos.openShift(amount);
    }
    await reload();
  });

  Future<void> scan() async {
    final code = await scanCode(context);
    if (code != null && mounted) await add(code);
  }

  Future<void> add(String input) async {
    await task(() async {
      final code = input.trim();
      if (code.isEmpty || code.length > 64) {
        throw const FormatException('Проверьте штрихкод.');
      }
      try {
        final product = await pos.cashierProduct(code);
        if (!mounted) return;
        final lots = (product['lots'] as List)
            .cast<Json>()
            .where((lot) => (lot['quantity'] as num) > 0)
            .toList();
        if (lots.isEmpty) {
          throw const FormatException('Товар отсутствует в продаже.');
        }
        final label = product['priceLabel'] as Json?;
        Json? lot;
        num? quantity = label?['weight'] as num?;
        if (label != null) {
          for (final entry in lots) {
            if (entry['lotId'] == label['lotId']) lot = entry;
          }
        } else if (lots.length == 1 &&
            product['requiresLotVerification'] != true) {
          lot = lots.single;
        } else {
          lot = await showDialog<Json>(
            context: context,
            builder: (context) => SimpleDialog(
              title: const Text('Сверьте партию и срок на упаковке'),
              children: lots
                  .map(
                    (entry) => SimpleDialogOption(
                      onPressed: () => Navigator.pop(context, entry),
                      child: Text(
                        'Годен до ${entry['expiryDate'] ?? '—'} · остаток ${entry['quantity']}\n'
                        'Партия ${entry['lotId']}',
                      ),
                    ),
                  )
                  .toList(),
            ),
          );
        }
        if (lot == null || !mounted) return;
        final factor = (product['quantityFactor'] as num?) ?? 1;
        if (quantity == null && product['unit'] == 'piece') quantity = factor;
        if (quantity == null) {
          final entered = await inputValue(
            context,
            'Количество товара',
            'Масса или объём',
            numeric: true,
          );
          if (entered == null) return;
          quantity = parseQuantity(entered);
        }
        if (quantity == null || quantity <= 0) {
          throw const FormatException('Укажите положительное количество.');
        }
        final available = lot['quantity'] as num;
        final lotId = lot['lotId'];
        final existing = lines
            .where((line) => line.lot['lotId'] == lotId)
            .toList();
        if (label != null &&
            existing.any(
              (line) => line.label?['labelId'] == label['labelId'],
            )) {
          throw const FormatException('Эта этикетка уже есть в чеке.');
        }
        final used = existing.fold<num>(0, (sum, line) => sum + line.quantity);
        if (used + quantity > available) {
          throw const FormatException('В партии недостаточно товара.');
        }
        final line = CartLine(product, lot, quantity);
        if (label != null &&
            (line.unitPrice * quantity * 100).round() !=
                ((label['printedTotal'] as num) * 100).round()) {
          throw FormatException(
            'Цена на этикетке ${money(label['printedTotal'] as num)}. '
            'Текущая цена ${money((line.unitPrice * quantity * 100).round() / 100)}. Нужна новая этикетка.',
          );
        }
        final same = existing
            .where(
              (item) =>
                  item.label == null &&
                  item.item['priceVersion'] == line.item['priceVersion'] &&
                  item.item['lotPriceVersion'] ==
                      line.item['lotPriceVersion'] &&
                  item.item['promotionVersion'] ==
                      line.item['promotionVersion'],
            )
            .firstOrNull;
        if (label == null && same != null) {
          same.quantity += quantity;
        } else {
          lines.add(line);
        }
        receipt = null;
      } finally {
        if (mounted) {
          barcode.clear();
          codeFocus.requestFocus();
        }
      }
    });
  }

  Future<void> payment() => task(() async {
    final total = await pos.quote(shiftId, lines);
    if (!mounted) return;
    final result = await showDialog<Payment>(
      context: context,
      builder: (_) => PaymentDialog(total),
    );
    if (result == null) return;
    final finalTotal = await pos.quote(shiftId, lines);
    if (finalTotal != total) {
      throw const FormatException('Итог изменился. Сверьте полученную сумму.');
    }
    try {
      receipt = await pos.sell(shiftId, lines, total, result.tendered);
      lines.clear();
    } finally {
      pending = await pos.pendingSale();
    }
    shift = await pos.activeShift();
  });

  Future<void> recover() => task(() async {
    try {
      if (pending != null) {
        receipt = await pos.recoverSale(pending!);
        lines.clear();
      }
      if (pendingMovement != null) await pos.recoverMovement(pendingMovement!);
    } finally {
      pending = await pos.pendingSale();
      pendingMovement = await pos.pendingMovement();
    }
    shift = await pos.activeShift();
  });

  Future<void> cash(String direction) => task(() async {
    final entered = await inputValue(
      context,
      direction == 'in' ? 'Внести наличные' : 'Изъять наличные',
      'Сумма',
      numeric: true,
    );
    if (entered == null || !mounted) return;
    final amount = parseAmount(entered);
    if (amount == null || amount <= 0) {
      throw const FormatException('Введите положительную сумму.');
    }
    final reason = await inputValue(context, 'Движение наличных', 'Причина');
    if (reason == null) return;
    if (reason.isEmpty || reason.length > 500) {
      throw const FormatException('Укажите причину.');
    }
    try {
      await pos.moveCash(shiftId, direction, amount, reason);
    } finally {
      pendingMovement = await pos.pendingMovement();
    }
    shift = await pos.activeShift();
    status = 'Движение наличных записано.';
  });

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text(
        pos.demo ? 'Учебная касса' : 'Касса · тестовый магазин',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 12),
      if (busy) const LinearProgressIndicator(),
      if (status != null) StatusBox(status!, error: failed),
      if (!pos.demo && !ready)
        Section(
          child: Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              FilledButton(
                onPressed: busy ? null : configure,
                child: const Text('Ключ кассы'),
              ),
              OutlinedButton(
                onPressed: busy ? null : () => task(reload),
                child: const Text('Проверить доступ'),
              ),
            ],
          ),
        ),
      if (locked)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const Text('Проверьте результат предыдущей операции.'),
              FilledButton(
                onPressed: busy ? null : recover,
                child: const Text('Проверить операцию'),
              ),
            ],
          ),
        ),
      if (ready)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                shift == null ? 'Смена закрыта' : 'Смена открыта',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              if (shift != null)
                Text(
                  'В кассе ${money((shift!['expectedCash'] as num?) ?? 0)} · '
                  'безнал ${money((shift!['manualCashlessSales'] as num?) ?? 0)}',
                ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  OutlinedButton(
                    onPressed: busy || locked || lines.isNotEmpty
                        ? null
                        : changeShift,
                    child: Text(
                      shift == null ? 'Открыть смену' : 'Закрыть смену',
                    ),
                  ),
                  if (shift != null) ...[
                    OutlinedButton(
                      onPressed: busy || locked ? null : () => cash('in'),
                      child: const Text('Внести'),
                    ),
                    OutlinedButton(
                      onPressed: busy || locked ? null : () => cash('out'),
                      child: const Text('Изъять'),
                    ),
                  ],
                ],
              ),
            ],
          ),
        ),
      if (shift != null && ready)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextField(
                controller: barcode,
                focusNode: codeFocus,
                enabled: !busy && !locked,
                decoration: InputDecoration(
                  labelText: 'Штрихкод',
                  suffixIcon: IconButton(
                    tooltip: 'Добавить по штрихкоду',
                    onPressed: busy || locked ? null : () => add(barcode.text),
                    icon: const Icon(Icons.arrow_forward),
                  ),
                ),
                onSubmitted: busy || locked ? null : add,
              ),
              const SizedBox(height: 8),
              FilledButton.tonalIcon(
                onPressed: busy || locked ? null : scan,
                icon: const Icon(Icons.qr_code_scanner),
                label: const Text('Сканировать штрихкод'),
              ),
              for (final line in lines)
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text(line.name),
                  subtitle: Text('${line.quantity} × ${money(line.unitPrice)}'),
                  trailing: IconButton(
                    tooltip: 'Убрать из чека',
                    icon: const Icon(Icons.delete_outline),
                    onPressed: busy || locked
                        ? null
                        : () => setState(() => lines.remove(line)),
                  ),
                ),
              const SizedBox(height: 8),
              FilledButton(
                onPressed: busy || locked || lines.isEmpty ? null : payment,
                child: const Text('К оплате'),
              ),
            ],
          ),
        ),
      if (receipt case final result?)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Чек · ${result['receiptMark'] ?? 'Тестовый'}'),
              for (final item in (result['lines'] as List).cast<Json>())
                Text(
                  '${item['name']} · ${item['quantity']} · ${money(item['lineTotal'] as num)}',
                ),
              Text('Итого ${money(result['total'] as num)}'),
              if (result['changeDue'] case final num change)
                Text('Сдача ${money(change)}')
              else
                const Text('Оплачено безналичными'),
              Text('№ ${result['operationId']}'),
            ],
          ),
        ),
    ],
  );
}

class Payment {
  const Payment(this.tendered);
  final num? tendered;
}

class PaymentDialog extends StatefulWidget {
  const PaymentDialog(this.total, {super.key});
  final num total;
  @override
  State<PaymentDialog> createState() => _PaymentDialogState();
}

class _PaymentDialogState extends State<PaymentDialog> {
  bool cash = true;
  num? tendered;
  bool confirmed = false;

  @override
  Widget build(BuildContext context) => AlertDialog(
    title: Text('К оплате ${money(widget.total)}'),
    content: SingleChildScrollView(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Wrap(
            spacing: 8,
            children: [
              ChoiceChip(
                label: const Text('Наличными'),
                selected: cash,
                onSelected: (_) => setState(() => cash = true),
              ),
              ChoiceChip(
                label: const Text('Безналичными'),
                selected: !cash,
                onSelected: (_) => setState(() => cash = false),
              ),
            ],
          ),
          const SizedBox(height: 12),
          if (cash)
            TextField(
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              decoration: const InputDecoration(
                labelText: 'Получено наличными',
              ),
              onChanged: (value) =>
                  setState(() => tendered = parseAmount(value)),
            )
          else
            CheckboxListTile(
              value: confirmed,
              title: const Text('Оплата получена'),
              contentPadding: EdgeInsets.zero,
              onChanged: (value) => setState(() => confirmed = value == true),
            ),
        ],
      ),
    ),
    actions: [
      TextButton(
        onPressed: () => Navigator.pop(context),
        child: const Text('Отмена'),
      ),
      FilledButton(
        onPressed: (cash ? (tendered ?? -1) >= widget.total : confirmed)
            ? () => Navigator.pop(context, Payment(cash ? tendered : null))
            : null,
        child: const Text('Оформить чек'),
      ),
    ],
  );
}

class ReceivingScreen extends StatefulWidget {
  const ReceivingScreen(this.pos, {super.key});
  final Pos pos;
  @override
  State<ReceivingScreen> createState() => _ReceivingScreenState();
}

class _ReceivingScreenState extends State<ReceivingScreen>
    with Tasks<ReceivingScreen> {
  Pos get pos => widget.pos;
  final name = TextEditingController();
  final barcode = TextEditingController();
  final price = TextEditingController();
  final stock = TextEditingController();
  bool expiry = false;

  @override
  void dispose() {
    name.dispose();
    barcode.dispose();
    price.dispose();
    stock.dispose();
    super.dispose();
  }

  Future<void> scan() async {
    final result = await scanCode(context);
    if (mounted && result != null) barcode.text = result;
  }

  Future<void> save() => task(() async {
    final title = name.text.trim();
    final code = barcode.text.trim();
    final salePrice = parseAmount(price.text);
    final quantity = parseQuantity(stock.text);
    if (title.isEmpty ||
        title.length > 200 ||
        code.isEmpty ||
        code.length > 64) {
      throw const FormatException('Заполните название и штрихкод.');
    }
    if (pos.demo &&
        (salePrice == null ||
            salePrice <= 0 ||
            quantity == null ||
            quantity % 1 != 0)) {
      throw const FormatException(
        'Введите цену больше нуля и целое количество.',
      );
    }
    await pos.addProduct(title, code, expiry, salePrice, quantity);
    if (!mounted) return;
    name.clear();
    barcode.clear();
    price.clear();
    stock.clear();
    expiry = false;
    status = pos.demo
        ? 'Товар добавлен в учебный каталог.'
        : 'Карточка отправлена администратору.';
  });

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text(
        pos.demo ? 'Учебная приёмка' : 'Приёмка · тестовый магазин',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 12),
      if (busy) const LinearProgressIndicator(),
      if (status != null) StatusBox(status!, error: failed),
      Section(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'Добавить товар',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: name,
              enabled: !busy,
              maxLength: 200,
              decoration: const InputDecoration(
                labelText: 'Название',
                counterText: '',
              ),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: barcode,
              enabled: !busy,
              maxLength: 64,
              decoration: InputDecoration(
                labelText: 'Штрихкод',
                counterText: '',
                suffixIcon: IconButton(
                  onPressed: busy ? null : scan,
                  tooltip: 'Сканировать штрихкод',
                  icon: const Icon(Icons.qr_code_scanner),
                ),
              ),
            ),
            if (pos.demo) ...[
              const SizedBox(height: 8),
              TextField(
                controller: price,
                enabled: !busy,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                decoration: const InputDecoration(labelText: 'Цена'),
              ),
              const SizedBox(height: 8),
              TextField(
                controller: stock,
                enabled: !busy,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(
                  labelText: 'Начальный остаток',
                ),
              ),
            ],
            CheckboxListTile(
              value: expiry,
              contentPadding: EdgeInsets.zero,
              title: const Text('Обязательный срок годности'),
              onChanged: busy
                  ? null
                  : (value) => setState(() => expiry = value == true),
            ),
            FilledButton(
              onPressed: busy ? null : save,
              child: Text(pos.demo ? 'Сохранить товар' : 'Отправить карточку'),
            ),
          ],
        ),
      ),
      Section(
        child: OutlinedButton.icon(
          onPressed: busy
              ? null
              : () => Navigator.of(context).push(
                  MaterialPageRoute<void>(builder: (_) => DraftScreen(pos)),
                ),
          icon: const Icon(Icons.receipt_long),
          label: const Text('Черновик накладной'),
        ),
      ),
    ],
  );
}

class DraftScreen extends StatefulWidget {
  const DraftScreen(this.pos, {super.key});
  final Pos pos;
  @override
  State<DraftScreen> createState() => _DraftScreenState();
}

class _DraftScreenState extends State<DraftScreen> with Tasks<DraftScreen> {
  final invoice = TextEditingController();
  final supplier = TextEditingController();
  final barcode = TextEditingController();
  final lines = <Json>[];
  DateTime date = DateTime.now();
  bool ready = false;

  @override
  void initState() {
    super.initState();
    task(() async {
      final draft = await widget.pos.readDraft();
      if (!mounted) return;
      if (draft != null) {
        invoice.text = draft['invoice'] as String;
        supplier.text = draft['supplier'] as String;
        date = DateTime.parse(draft['date'] as String);
        lines.addAll((draft['lines'] as List).cast<Json>());
      }
      ready = true;
    });
  }

  @override
  void dispose() {
    invoice.dispose();
    supplier.dispose();
    barcode.dispose();
    super.dispose();
  }

  Future<void> scan() async {
    final result = await scanCode(context);
    if (mounted && result != null) barcode.text = result;
  }

  Future<void> edit({int? index}) => task(() async {
    final old = index == null ? null : lines[index];
    final code = barcode.text.trim();
    if (old == null && (code.isEmpty || code.length > 64)) {
      throw const FormatException('Введите штрихкод товара.');
    }
    final product = old == null
        ? await widget.pos.receivingProduct(code)
        : null;
    if (!mounted) return;
    final unit = (old ?? product!)['unit'];
    final raw = await inputValue(
      context,
      'Количество в партии',
      'Количество',
      numeric: true,
      initial: old?['quantity'].toString(),
    );
    if (raw == null || !mounted) return;
    final quantity = parseQuantity(raw);
    if (quantity == null ||
        quantity <= 0 ||
        (unit == 'piece' && quantity % 1 != 0)) {
      throw const FormatException(
        'Проверьте количество для единицы измерения товара.',
      );
    }
    final requiresExpiry = (old ?? product!)['requiresExpiry'] == true;
    final entered = await inputValue(
      context,
      'Срок годности партии',
      'ГГГГ-ММ-ДД',
      initial: old?['expiryDate'] as String?,
    );
    if (entered == null || !mounted) return;
    final expiry = DateTime.tryParse(entered);
    if ((requiresExpiry || entered.isNotEmpty) &&
        (expiry == null || dateText(expiry) != entered)) {
      throw const FormatException('Укажите срок в формате ГГГГ-ММ-ДД.');
    }
    final item = <String, dynamic>{
      'productId': (old ?? product!)['productId'],
      'name': (old ?? product!)['name'],
      'requiresExpiry': requiresExpiry,
      'unit': unit,
      'lotId': old?['lotId'] ?? newId(),
      'quantity': quantity,
      'expiryDate': entered.isEmpty ? null : entered,
    };
    if (index == null) {
      lines.add(item);
      barcode.clear();
    } else {
      lines[index] = item;
    }
  });

  Future<void> save() => task(() async {
    await widget.pos.saveDraft({
      'invoice': invoice.text.trim(),
      'supplier': supplier.text.trim(),
      'date': dateText(date),
      'lines': lines,
    });
    status = 'Черновик сохранён на устройстве.';
  });

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Черновик накладной')),
    body: SafeArea(
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (busy) const LinearProgressIndicator(),
          if (status != null) StatusBox(status!, error: failed),
          if (ready) ...[
            Section(
              child: Column(
                children: [
                  TextField(
                    controller: supplier,
                    enabled: !busy,
                    decoration: const InputDecoration(labelText: 'Поставщик'),
                  ),
                  const SizedBox(height: 8),
                  TextField(
                    controller: invoice,
                    enabled: !busy,
                    decoration: const InputDecoration(
                      labelText: 'Номер накладной',
                    ),
                  ),
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    title: Text('Дата · ${dateText(date)}'),
                    trailing: const Icon(Icons.calendar_month),
                    onTap: busy
                        ? null
                        : () async {
                            final selected = await showDatePicker(
                              context: context,
                              initialDate: date,
                              firstDate: DateTime(2000),
                              lastDate: DateTime(2100),
                            );
                            if (selected != null && mounted) {
                              setState(() => date = selected);
                            }
                          },
                  ),
                ],
              ),
            ),
            Section(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  TextField(
                    controller: barcode,
                    enabled: !busy,
                    decoration: InputDecoration(
                      labelText: 'Штрихкод',
                      suffixIcon: IconButton(
                        onPressed: busy ? null : scan,
                        tooltip: 'Сканировать штрихкод',
                        icon: const Icon(Icons.qr_code_scanner),
                      ),
                    ),
                  ),
                  OutlinedButton(
                    onPressed: busy || lines.length >= 100
                        ? null
                        : () => edit(),
                    child: const Text('Добавить партию'),
                  ),
                  for (var index = 0; index < lines.length; index++)
                    ListTile(
                      contentPadding: EdgeInsets.zero,
                      title: Text(lines[index]['name'] as String),
                      subtitle: Text(
                        '${lines[index]['quantity']} · срок ${lines[index]['expiryDate'] ?? '—'}',
                      ),
                      onTap: busy ? null : () => edit(index: index),
                      trailing: IconButton(
                        tooltip: 'Удалить партию',
                        icon: const Icon(Icons.delete_outline),
                        onPressed: busy
                            ? null
                            : () => setState(() => lines.removeAt(index)),
                      ),
                    ),
                ],
              ),
            ),
            FilledButton(
              onPressed: busy ? null : save,
              child: const Text('Сохранить черновик'),
            ),
          ],
        ],
      ),
    ),
  );
}

class AdminScreen extends StatefulWidget {
  const AdminScreen(this.pos, {super.key});
  final Pos pos;
  @override
  State<AdminScreen> createState() => _AdminScreenState();
}

class _AdminScreenState extends State<AdminScreen> with Tasks<AdminScreen> {
  ServerApi get api => widget.pos.api;
  List<Json> proposals = [];
  bool loaded = false;

  @override
  void initState() {
    super.initState();
    task(load);
  }

  Future<void> load() async {
    final data = await api.request(
      'GET',
      '${api.store}/receiving/catalog/proposals',
    );
    proposals = (data is List ? data : (data as Json)['items'] as List)
        .cast<Json>();
    loaded = true;
  }

  Future<void> approve(Json item) => task(() async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Утвердить карточку товара?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Отмена'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Утвердить'),
          ),
        ],
      ),
    );
    if (confirm != true) return;
    await api.request(
      'POST',
      '${api.store}/catalog/proposals/${item['proposalId']}/approve',
      body: {},
    );
    await load();
    status = 'Карточка утверждена. Теперь назначьте цену.';
  });

  Future<void> setPrice(Json item) => task(() async {
    final path = '${api.store}/catalog/products/${item['publishedProductId']}';
    final card = await api.request('GET', path) as Json;
    if (!mounted) return;
    final raw = await inputValue(
      context,
      'Цена товара',
      'Цена в сумах',
      numeric: true,
      initial: card['unitPrice']?.toString(),
    );
    if (raw == null) return;
    final amount = parseAmount(raw);
    if (amount == null || amount <= 0) {
      throw const FormatException('Укажите цену больше нуля.');
    }
    await api.request(
      'PUT',
      '$path/price',
      body: {
        'expectedVersion': card['priceVersion'],
        'unitPrice': amount,
        'active': true,
        'reason': 'Назначение цены в Android',
      },
    );
    status = 'Цена сохранена.';
  });

  @override
  Widget build(BuildContext context) => ListView(
    padding: const EdgeInsets.all(16),
    children: [
      Text(
        'Карточки товаров',
        style: Theme.of(context).textTheme.headlineSmall,
      ),
      const SizedBox(height: 12),
      if (busy) const LinearProgressIndicator(),
      if (status != null) StatusBox(status!, error: failed),
      OutlinedButton.icon(
        onPressed: busy ? null : () => task(load),
        icon: const Icon(Icons.refresh),
        label: const Text('Обновить'),
      ),
      const SizedBox(height: 8),
      if (loaded && proposals.isEmpty)
        const StatusBox('Карточек на проверку пока нет.'),
      for (final item in proposals)
        Section(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                (item['product'] as Json)['name'] as String,
                style: Theme.of(context).textTheme.titleMedium,
              ),
              if (item['status'] == 'pending')
                FilledButton(
                  onPressed: busy ? null : () => approve(item),
                  child: const Text('Утвердить'),
                )
              else if (item['publishedProductId'] != null)
                OutlinedButton(
                  onPressed: busy ? null : () => setPrice(item),
                  child: const Text('Назначить цену'),
                )
              else
                const Text('Карточка закрыта'),
            ],
          ),
        ),
    ],
  );
}
