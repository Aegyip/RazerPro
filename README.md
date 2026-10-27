# Barracuda Tray

Razer Barracuda X Chroma için hafif bir Windows tray uygulaması. Razer Synapse, Razer Audio ve Razer 7.1 uygulamalarının yerine geçer.

## Durum

| Faz | İçerik | Durum |
|---|---|---|
| 1 | RGB: aç/kapat, renk, parlaklık, Nefes / Gökkuşağı efektleri | ✅ |
| 2 | 7.1 sanal surround (kendi motoru + HeSuVi profilleri + VB-CABLE) | ✅ ölçüldü (~17 ms); kulakla karşılaştırma bekliyor |
| 3 | Pil, şarj, mikrofon durumu; düşük pil ve şarj bitti bildirimleri | ✅ |
| 4 | EQ ön ayarı ve 10 bantlık özel EQ, sidetone, güç tasarrufu, mikrofon susturma | ✅ komutlar kulaklıkta doğrulandı |
| 5 | Profiller, Windows ile başlama, genel kısayollar | ✅ |

Rahatsız Etmeyin modu yalnızca gösteriliyor: kulaklık onu sadece Razer Audio mobil uygulamasından (Bluetooth) değiştirilebilir olarak bildiriyor; dongle üzerinden yazma komutu bilinmiyor.

## Nasıl çalışır

### Işıklar

Kulaklığın ışıkları standart **HID LampArray** (usage page `0x59`) arayüzüyle kontrol ediliyor. Uygulama feature report'ları doğrudan dongle'a (`1532:0574`) gönderir. Bu yüzden ne Razer yazılımına ne de Windows Dinamik Aydınlatma'ya ihtiyaç var.

- Renkler `LampMultiUpdateReport` (`0x34`) ile gönderiliyor. Bu firmware `LampRangeUpdateReport`'u (`0x35`) yok sayıyor.
- Feature report'lar kendi gerçek uzunluklarında gönderiliyor. Windows normalde hepsini en uzun raporun boyuna (51 byte) dolduruyor.
- Kulaklık kapanıp açıldığında renkleri unuttuğu için sabit renk her 5 saniyede bir ve kulaklık yeniden bağlandığı anda gönderiliyor. Efektler 25 fps çalışıyor.

### Kulaklık durumu ve ayarları

Razer'ın vendor kanalı: usage page `0xFF14`, 64 byte'lık rapor `0x02` (düzen `Headset/RazerVendorChannel.cs` içinde). Kulaklık bağlantı, pil, şarj, mikrofon, Chroma ve Rahatsız Etmeyin olaylarını kendiliğinden gönderiyor. Uygulama bağlantı kurulunca aşağıdaki ayarları bir kez okuyor.

| Özellik | Okuma | Yazma | Değerler |
|---|---|---|---|
| `0x13` EQ ön ayarı | ✅ | ✅ `0x93` | Oyun `07`, Müzik `08`, Film `09`, Özel `FF` |
| `0x15` EQ eğrisi | ✅ | ✅ `0x95` | 10 işaretli byte (31 Hz … 16 kHz); düzenleyici ±6 ile sınırlı |
| `0x18` sidetone açık | ✅ | `0x98` | 0 / 1 |
| `0x19` sidetone seviyesi | ✅ | ✅ `0x99` | 0–15 (Synapse: `round(yüzde × 15 / 100)`) |
| `0x21` pil | ✅ | — | yüzde |
| `0x2A` şarj oluyor | ✅ | — | 0 / 1; pil dolunca kablo takılıyken de 0 olur |
| `0x2C` güç tasarrufu | ✅ | ✅ `0xAC` | dakika; 0 = kapalı (menüde yalnızca doğrulanan 0/15/30) |

Mikrofon susturma Razer kanalından değil, Windows'un ses API'siyle (USB Audio Class mute) yapılıyor; Synapse da böyle yapıyor.

### 7.1 sanal surround

Kulaklığın ses cihazı Windows'ta yalnızca stereo olduğu için oyunlar ona 7.1 gönderemez. Razer bunu kendi sanal 7.1 cihazı ve servisiyle çözüyor; bu uygulama da aynı yolu açık parçalarla izliyor:

```
oyun ─7.1─► CABLE Input ══ VB-CABLE ══► CABLE Output ─► motor ─stereo─► kulaklık
            (varsayılan cihaz)                          (HeSuVi profili)
```

- 7.1 açılınca varsayılan ses cihazı **CABLE Input** yapılır; kapanınca ya da uygulamadan çıkınca kulaklığa geri verilir. İletişim cihazına dokunulmaz, sesli sohbet uygulamaları kulaklıkta kalır.
- Motor, 7.1 sesi HeSuVi profiliyle iki kulağa çevirir. Kanal düzeni ve karıştırma kuralları HeSuVi'nin kaynak kodundan alındı: LFE 111 Hz'de kesilip merkeze eklenir, 7 kanallı profillerde sağ taraf solun aynasıdır. Equalizer APO gerekmez.
- Gecikme için: 128 örneklik bölümlenmiş FFT konvolüsyonu (2,7 ms), iki kulak tek ters FFT'de; VB-CABLE'ın kayıt tarafı **özel modda** 8 kanal olarak en küçük periyotta okunur (Windows'taki kayıt formatı ayarı önemsizdir); kulaklığa da **özel modda** (Windows karıştırıcısını atlayarak) yazılır, olmazsa paylaşımlı moda düşülür; ara kuyruk küçük başlar ve yalnızca kesinti olursa büyür. Ölçülen: ~17,5 ms (3 ms okuma + 2,7 ms blok + ~9 ms kuyruk + 3 ms çıkış). Oyunun CABLE'a yazarkenki Windows gecikmesi (~10 ms) buna dahil değildir; o her sanal 7.1 çözümünde vardır.
- Profiller `%APPDATA%\BarracudaTray\hrir` klasöründen okunur (HeSuVi 2.0.0.1 kurulum paketinin `hrir` klasörü, yalnızca 48 kHz dosyalar). Profil dosyaları depoya konmaz.

Kurulum: [VB-CABLE](https://vb-audio.com/Cable/)'ı yönetici olarak kurup bilgisayarı yeniden başlatın, sonra Ses ayarları → Kayıttan yürütme → **CABLE Input** → Yapılandır → **7.1 Surround** seçin.

### Bilinen tuzaklar

- **Dongle takılı olsa da kulaklık Bluetooth'ta olabilir.** O zaman dongle komutları kabul ediyor ama kulaklığa iletmiyor. Dongle'a geçmek için kulaklık açıkken SmartSwitch'e 2 kez basın.
- **Chroma kulaklığın içinde kapatılabiliyor.** SmartSwitch'e 3 kez basmak açıp kapatıyor. Kapalıyken bilgisayardan gönderilen hiçbir renk görünmüyor.
- **Desteklenmeyen bir özelliği sormak dongle'ı kilitliyor.** `0x20` sorgusundan sonra dongle hiçbir output report'u kabul etmedi; çıkarıp takmak gerekti. Yalnızca yukarıdaki tablodaki özellikler soruluyor; her yazma 1 saniyelik zaman aşımıyla iptal edilebiliyor.
- **Yazma onayı değer değildir.** Kulaklık bir yazmayı `özellik | 0x80` kimliğiyle ve `00` durum byte'ıyla onaylıyor; uygulama bu onayları değer olarak okumuyor, yazdıktan sonra özelliği yeniden soruyor.
- **Uygulamaya sabitlenmiş ses cihazları 7.1'i atlar.** Windows bazı uygulamaları belirli bir cihaza sabitleyebiliyor (Ayarlar → Ses → Ses karıştırıcısı). Bu uygulamalar varsayılan cihaz değişince CABLE'a geçmez; özel mod açıkken kulaklığa da ulaşamaz. Ses karıştırıcısındaki **Sıfırla** düğmesi bu sabitlemeleri kaldırır.
- **Razer uygulamaları arka planda çalışıyorsa** Synapse ışıkları yönetir, Razer'ın 7.1 cihazı da uygulamaları kendine sabitleyebilir. Razer uygulamalarını kaldırın.
- **Windows Dinamik Aydınlatma** bu cihazda açıksa çakışabilir. Ayarlar → Kişiselleştirme → Dinamik Aydınlatma bölümünden kulaklık için kapatın.

## Kullanım

Tray simgesine sağ tıklayınca menü açılır, çift tıklamak ışıkları açar/kapatır.

| Kısayol | İşlev |
|---|---|
| Ctrl+Alt+L | Işıkları aç/kapat |
| Ctrl+Alt+S | 7.1'i aç/kapat |
| Ctrl+Alt+M | Mikrofonu sustur/aç |

Başka bir program aynı kısayolu kullanıyorsa o kısayol menüde görünmez. **Profiller** menüsü ışık, 7.1 ve kulaklık ses ayarlarının (EQ, sidetone) o anki halini adıyla kaydeder. **Windows ile başlat** kullanıcı başına `Run` anahtarını kullanır, yönetici izni gerekmez. Ayarlar `%APPDATA%\BarracudaTray\settings.json` dosyasında saklanır.

Test için komut satırı modları:

```
BarracudaTray.exe --diag            # HID koleksiyonları ve alan düzeni
BarracudaTray.exe --color FF0000    # tüm lambaları tek renge ayarla
BarracudaTray.exe --spread          # her lambaya farklı renk (lamba sırası kontrolü)
BarracudaTray.exe --off             # ışıkları kapat
BarracudaTray.exe --auto            # kulaklığın kendi efektine geri dön
BarracudaTray.exe --read 31         # bir feature report'u oku
BarracudaTray.exe --listen 30       # kulaklığın gönderdiği raporları 30 sn yazdır
BarracudaTray.exe --poll            # her input report'u bir kez iste
BarracudaTray.exe --status          # kulaklık izleyicisinin okuduğu durum ve ayarlar
BarracudaTray.exe --get 13          # vendor özelliği sor (yalnızca tablodakiler)
BarracudaTray.exe --set 13 07       # vendor özelliği yaz (yalnızca tablodakiler)
BarracudaTray.exe --devices         # ses cihazları, formatları ve özel modda desteklenenler
BarracudaTray.exe --meters 10       # hangi ses cihazından ses geçtiğini 10 sn göster
BarracudaTray.exe --selftest razer  # konvolüsyonu bir profile karşı doğrula
BarracudaTray.exe --surround 10     # 7.1 motorunu 10 sn çalıştır, gecikme dökümünü yazdır (shared: paylaşımlı mod)
```

## Derleme

.NET 10 SDK gerekir.

```
dotnet build -c Release
```
