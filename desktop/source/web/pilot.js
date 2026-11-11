import {isSaleBarcode, scaleQuantity} from './model.js';
import {language, t} from './i18n.js';

export function mountPilot({bridge, $, notice, element, setBusy}) {
  const picker = $('ui-language');
  picker.value = language;
  if (language === 'uz' && bridge) bridge('locale', language).catch(() => {});
  picker.addEventListener('change', async () => {
    if (!window.confirm(t('Интерфейс перезагрузится. Сохраните открытые формы. Для стенда потребуется заново войти.'))) {
      picker.value = language;
      return;
    }
    try {
      if (bridge) await bridge('locale.change', picker.value);
      localStorage.setItem('kasa-language', picker.value);
      location.reload();
    } catch {
      picker.value = language;
      notice($('device-printer-status'), t('Не удалось применить настройку.'));
    }
  });
  $('device-scan-form').addEventListener('submit', event => {
    event.preventDefault();
    const code = $('device-scan').value.trim();
    notice($('device-scan-status'), isSaleBarcode(code) ? t('Штрихкод принят: ') + code :
      t('Введите цифровой штрихкод, код партии LOT или код весовой этикетки WPL. Настройте завершение ввода клавишей Enter.'));
    $('device-scan').select();
  });
  $('device-scale-form').addEventListener('submit', event => {
    event.preventDefault();
    const weight = scaleQuantity($('device-scale').value);
    notice($('device-scale-status'), weight ? t('Вес принят: ') + new Intl.NumberFormat(language === 'uz' ? 'uz-Latn' : 'ru-RU',
      {maximumFractionDigits:3}).format(weight / 1000) + t(' кг') :
      t('Введите вес в килограммах или граммах. Например: 0,375 kg или 375 g.'));
    $('device-scale').select();
  });
  $('device-printers').addEventListener('click', async () => {
    const badge = $('printer-status');
    setBusy(true);
    try {
      const result = await bridge('device.printers');
      $('device-printer-list').replaceChildren(...(result?.installed ?? []).map(printer => element('li', printer)));
      badge.textContent = result?.defaultPrinter ? t('● Принтер назначен') : t('◷ Принтер не назначен');
      badge.className = 'badge ' + (result?.defaultPrinter ? 'green' : 'gold');
      badge.title = result?.defaultPrinter || '';
      notice($('device-printer-status'), result?.defaultPrinter ? t('Принтер по умолчанию: ') + result.defaultPrinter :
        t('Принтер по умолчанию не определён.'));
    } catch {
      badge.textContent = t('! Список принтеров недоступен');
      badge.className = 'badge red';
      badge.title = '';
      $('device-printer-list').replaceChildren();
      notice($('device-printer-status'), t('Не удалось прочитать список принтеров Windows.'));
    } finally { setBusy(false); }
  });
  const driverStatus = $('device-driver-status');
  const driverModel = $('device-driver-model');
  const openDriver = $('device-open-driver');
  const officialDriver = $('device-official-driver');
  const driverTerms = $('device-driver-terms');
  let models = [];
  const selectedModel = () => models.find(model => model.id === driverModel.value);
  function renderDriverChoice() {
    const model = selectedModel();
    openDriver.disabled = !model;
    openDriver.textContent = model?.download === 'server' ? t('Установить драйвер с сервера') :
      model?.download === 'vendor' ? t('Скачать и установить драйвер') : t('Открыть драйвер производителя');
    officialDriver.hidden = !model?.download;
    driverTerms.hidden = !model?.terms;
    notice($('device-driver-source'), model?.download === 'server' ? t('Доступен пакет из каталога вашего сервера.') :
      model?.download === 'vendor' ? t('Пакет загружается напрямую у производителя.') :
      model ? t('Официальная страница драйвера') + ' · ' + model.host : t('Сверьте модель по наклейке принтера.'));
  }
  driverModel.addEventListener('change', renderDriverChoice);
  $('device-usb-printers').addEventListener('click', async () => {
    const list = $('device-usb-list');
    const catalog = $('device-driver-catalog');
    setBusy(true);
    notice(driverStatus, t('Ищем подключённые USB-принтеры…'));
    try {
      const result = await bridge('device.printerDrivers');
      if (!Array.isArray(result?.devices) || !Array.isArray(result?.models)) throw new Error();
      models = result.models;
      list.replaceChildren(...result.devices.map(device => {
        const status = device.status === 28 ? t('Драйвер не установлен') :
          device.status > 0 ? t('Код состояния Windows') + ' ' + device.status : '';
        const model = models.find(row => row.id === device.model);
        return element('li', [model?.name ?? device.name ?? t('USB-принтер'),
          device.hardwareId, status].filter(Boolean).join(' · '));
      }));
      list.hidden = !result.devices.length;
      driverModel.replaceChildren(element('option', t('Выберите модель')));
      driverModel.firstElementChild.value = '';
      for (const model of models) {
        const option = element('option', model.name);
        option.value = model.id;
        driverModel.append(option);
      }
      if (result.devices.length && result.devices[0].model &&
          result.devices.every(device => device.model === result.devices[0].model))
        driverModel.value = result.devices[0].model;
      renderDriverChoice();
      catalog.hidden = false;
      const messages = [];
      if (!result.usbAvailable) messages.push(t('Windows не вернула список USB-устройств. Модель можно выбрать вручную.'));
      else if (!result.devices.length) messages.push(t('USB-принтеры не найдены. Проверьте питание и кабель или выберите модель вручную.'));
      if (!result.repositoryValid) messages.push(t('Проверьте файл PrinterDrivers.json. Доступны страницы производителей.'));
      notice(driverStatus, messages.join(' '));
    } catch {
      models = [];
      list.replaceChildren();
      list.hidden = catalog.hidden = true;
      notice(driverStatus, t('Не удалось проверить USB-принтеры.'));
    } finally { setBusy(false); }
  });
  async function launchDriver(action) {
    const model = selectedModel();
    if (!model || $('device-driver-actions').disabled) return;
    const errors = {
      printer_repository_invalid: t('Проверьте файл PrinterDrivers.json.'),
      printer_package_unavailable: t('Пакет этой модели отсутствует в каталоге сервера.'),
      printer_package_hash_mismatch: t('Контрольная сумма драйвера не совпала. Установщик заблокирован.'),
      printer_package_untrusted: t('Windows не подтвердила подпись установщика. Запуск заблокирован.'),
      printer_package_too_large: t('Размер установщика превышает 64 МиБ.'),
    };
    const installing = action === 'device.installPrinterDriver';
    setBusy(true);
    notice(driverStatus, installing ? t('Скачиваем и проверяем драйвер…') : '');
    try {
      const result = await bridge(action, model.id);
      if (result?.status === 'started')
        notice(driverStatus, t('Установщик запущен. После завершения нажмите «Проверить принтеры» и распечатайте учебный чек.'));
      else if (result?.status === 'cancelled') notice(driverStatus, t('Установка отменена.'));
      else if (result?.status === 'browser') notice(driverStatus, action === 'device.printerTerms' ? '' :
        t('В браузере открыта официальная страница драйвера.'));
      else throw new Error();
    } catch (error) {
      notice(driverStatus, errors[error.code] ?? (installing ? t('Не удалось скачать или запустить драйвер.') :
        t('Не удалось открыть страницу производителя.')));
    } finally { setBusy(false); }
  }
  openDriver.addEventListener('click', () => launchDriver(selectedModel()?.download ?
    'device.installPrinterDriver' : 'device.printerSupport'));
  officialDriver.addEventListener('click', () => launchDriver('device.printerSupport'));
  driverTerms.addEventListener('click', () => launchDriver('device.printerTerms'));
  $('device-driver-actions').disabled = !bridge;
  $('device-printers').disabled = !bridge;
}
