# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · **العربية** · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager هو تطبيق خفيف يبدأ بمنصة Windows لمراقبة خوادم Linux وفتح جلسات SSH مباشرة والتحكم الصريح في الاتصالات الآمنة بين الخوادم. صُمم للبنية الشخصية والمجموعات الصغيرة من دون لوحة ثقيلة أو Kubernetes أو API عام على كل عقدة.

## الميزات

- مراقبة CPU/load والذاكرة وswap والأقراص وinode والشبكة وuptime وزمن الاستجابة وSSH وWireGuard؛
- ملفات خوادم ومجموعات ووسوم ومفضلة وتنبيهات وسجل محلي قصير؛
- مفتاح SSH Ed25519 مستقل يبقى مفتاحه الخاص على جهاز Windows؛
- طرفية SSH مباشرة من دون إرسال المفتاح الخاص إلى Hub؛
- شبكة نجمية عبر Hub ذي IP عام، بينما تحتاج Nodes إلى اتصال صادر فقط؛
- روابط اتجاهية مثل `AI agent → Home server:22` يمكن فصلها بشكل مستقل؛
- سياسات `/32` وTCP/UDP والمنفذ والإصدار وTTL؛
- تسجيل لمرة واحدة وCSR وmTLS وفصل أدوار ومنع التكرار وتدقيق.

## البنية والتثبيت

يتصل عميل Windows عبر mTLS/HTTPS بـ Control Hub مبني على ASP.NET Core 10 وSQLite. ينشئ Linux Agent جلسات صادرة فقط. ينقل WireGuard البيانات وتمنع nftables العبور افتراضياً. الرابط أحادي الاتجاه، ولا يحتاج الخادم المنزلي خلف NAT إلى IP عام.

```bash
# نزّل ochenstarik-server-monitor-manager.sh أولاً من ملفات الإصدار.
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

افتح UDP `51820` وTCP `7443` على Hub، وأنشئ رموز Nodes وثبت بقية الخوادم بدور Node. ثم استخدم `install-control-hub` و`control-code` و`control-device-code` و`install-control-agent`. يختار المثبت `amd64` أو `arm64` ويتحقق من SHA-256.

## الأمان والحالة

لا توجد كلمة مرور root مشتركة ولا يغادر مفتاح WireGuard الخاص عقدته. هويات monitoring وterminal وAgent وOperator وأتمتة AI منفصلة. يستخدم SSH أمراً إجبارياً بلا shell أو PTY أو forwarding؛ يقيّد mTLS الصلاحيات؛ تسمح nftables بالروابط الصريحة فقط؛ ويحفظ SQLite الحالة المطلوبة والتدقيق قبل تعديل الجدار الناري.

الإصدار `v0.1.0-alpha.4` للاختبار. يتضمن فرع التطوير الحالي عميل Windows ومثبت Hub/Node وLinks وmTLS وإلغاء الشهادات وإعادة التسجيل وSQLite والتدقيق والأحداث ومخزناً محدوداً دون اتصال مع downsampling. المتبقي: مصالحة إعادة الاتصال، اختبار 50–100 Node ومثبت Windows موقّع.

## الترخيص

Copyright 2026 ochenstarik-ui. يتوفر المشروع بموجب [Apache License 2.0](../../LICENSE).

الوثائق: [البنية](../architecture.md)، [الأمان](../security-model.md)، [الخطة](../roadmap.md)، [عقد المثبت](../installer-contract.md).
