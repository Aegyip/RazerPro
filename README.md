# Barracuda Tray

Razer Barracuda X Chroma için hafif bir Windows tray uygulaması. Razer Synapse, Razer Audio ve Razer 7.1 uygulamalarının yerine geçer.

## Kurulum

`build-installer.ps1` çalıştırılınca `dist\BarracudaTray-Kurulum.zip` oluşur (~130 KB). Zip'i açıp **Kur.cmd**'ye çift tıklamak yeterli. Yönetici izni yalnızca .NET ya da VB-CABLE eksikse, onları kuran ayrı bir pencere için istenir; kullanıcıya ait her şey normal oturumda yapılır. Her çalışma `%TEMP%\BarracudaTray-kurulum.log` dosyasına yazılır. İndirmeler Windows'un `curl.exe` aracıyla yapılır (SourceForge, PowerShell'in indirme komutlarına dosya yerine HTML sayfası veriyor) ve dosya türü kontrol edilir.

| Adım | Ne yapılır | Ne zaman |
|---|---|---|
| .NET 10 Masaüstü Çalışma Zamanı | Microsoft'tan indirilip sessizce kurulur (~60 MB) | Yalnızca eksikse |
| VB-CABLE | vb-audio.com'dan indirilir, VB-Audio'nun kendi kurulum penceresi açılır; orada **Install Driver**'a basılır | Yalnızca eksikse |
| 7.1 profilleri | HeSuVi paketi SourceForge'dan indirilir, Windows'un `tar` aracıyla yalnızca `hrir` klasörü çıkarılır | Yalnızca eksikse |
| Uygulama | `%LOCALAPPDATA%\Programs\BarracudaTray` klasörüne kopyalanır, Başlat menüsüne kısayol eklenir, Windows ile başlaması ayarlanır | Her kurulumda (güncelleme de böyle yapılır) |
| CABLE Input | 7.1 (8 kanal) yapılır; sürücü yeni kurulduysa yeniden başlatmadan sonra uygulama bunu 7.1 açılınca kendisi yapar | Her kurulumda |
| Razer yazılımları | Kuruluysa listelenir, istenirse Razer'ın kaldırıcısı açılır | Kuruluysa |

VB-CABLE'ın lisansı, paketin başka bir yazılımın kurulumuna yazarın izni olmadan gömülmesine izin vermiyor. Bu yüzden kurulum VB-CABLE'ı sessizce kurmuyor, VB-Audio'nun kendi penceresini açıyor. Uygulamayı başkalarına dağıtacaksanız VB-Audio'dan izin alın. HeSuVi MIT lisanslıdır; profil dosyaları pakete konmaz, kurulumda resmi kaynaktan indirilir.

Kaldırmak için **Kaldir.cmd**: uygulamayı düzgün kapatır (varsayılan ses cihazı kulaklığa döner), dosyaları, kısayolu ve başlangıç kaydını siler. VB-CABLE ve .NET başka programlarca da kullanılabildiği için yerinde bırakılır.

## Durum

| Faz | İçerik | Durum |
|---|---|---|
| 1 | RGB: aç/kapat, renk, parlaklık, Nefes / Gökkuşağı efektleri | ✅ |
| 2 | 7.1 sanal surround (kendi motoru + HeSuVi profilleri + VB-CABLE) | ✅ ~23 ms, kesintisiz |
| 3 | Pil, şarj, mikrofon durumu; düşük pil ve şarj bitti bildirimleri | ✅ |
| 4 | EQ ön ayarı ve 10 bantlık özel EQ, sidetone, güç tasarrufu, mikrofon susturma | ✅ komutlar kulaklıkta doğrulandı |
| 5 | Profiller, Windows ile başlama, genel kısayollar | ✅ |

Rahatsız Etmeyin modu yalnızca gösteriliyor: kulaklık onu sadece Razer Audio mobil uygulamasından (Bluetooth) değiştirilebilir olarak bildiriyor; dongle üzerinden yazma komutu bilinmiyor.

## Nasıl çalışır

### Işıklar

Kulaklığın ışıkları standart **HID LampArray** (usage page `0x59`) arayüzüyle kontrol ediliyor. Uygulama feature report'ları doğrudan dongle'a (`1532:0574`) gönderir. Bu yüzden ne Razer yazılımına ne de Windows Dinamik Aydınlatma'ya ihtiyaç var.

- Renkler `LampMultiUpdateReport` (`0x34`) ile gönderiliyor. Bu firmware `LampRangeUpdateReport`'u (`0x35`) yok sayıyor.
- Feature report'lar kendi gerçek uzunluklarında gönderiliyor. Windows normalde hepsini en uzun raporun boyuna (51 byte) dolduruyor.
- Kulaklık kapanıp açıldığında renkleri unuttuğu için sabit renk her 5 saniyede bir ve kulaklık yeniden bağlandığı anda gönderiliyor. Efektler 15 fps çalışıyor; ışık güncellemeleri sesle aynı 2.4 GHz bağlantıdan gidiyor.

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

- VB-CABLE'ın cihazları hep etkin kalır ve yeniden adlandırılır: çıkış **Barracuda 7.1**, kayıt tarafı **Barracuda 7.1 (motor)**; kullanılmayan "CABLE In 16ch" devre dışı bırakılır. Cihazları 7.1'le birlikte açıp kapatmak, Discord gibi uygulamaların her seferinde "yeni cihaz takıldı" sanıp geçiş önermesine yol açıyordu.
- 7.1 açılınca bütün varsayılan roller (iletişim dahil) **Barracuda 7.1** olur; kapanınca ya da uygulamadan çıkınca kulaklığa geri verilir.
- Windows'un çıkış listesinden **Barracuda 7.1** seçilince 7.1 kendiliğinden açılır; 7.1 açıkken başka bir cihaz seçilince 7.1 kapanır ve seçilen cihaz bütün rollerde varsayılan kalır.
- Motor, 7.1 sesi HeSuVi profiliyle iki kulağa çevirir. Kanal düzeni ve karıştırma kuralları HeSuVi'nin kaynak kodundan alındı: LFE 111 Hz'de kesilip merkeze eklenir, 7 kanallı profillerde sağ taraf solun aynasıdır. Equalizer APO gerekmez.
- Gecikme için: 128 örneklik bölümlenmiş FFT konvolüsyonu (2,7 ms), iki kulak tek ters FFT'de; VB-CABLE'ın kayıt tarafı **özel modda** 8 kanal olarak 3 ms periyotla okunur (Windows'taki kayıt formatı ayarı önemsizdir). Kulaklığa **paylaşımlı modda** yazılır ve her 3 ms'de bir beslenir; kulaklık tamponunda iki Windows periyodu (20 ms) tutulur, ara kuyruk ~0'da kalır. Ölçülen: ~23 ms sabit, kulaklığa saniyede tam 48 000 örnek, 60 sn'de açılıştaki tek kesinti dışında kesinti yok, tek çekirdeğin ~%5'i. Oyunun CABLE'a yazarkenki Windows gecikmesi (~10 ms) buna dahil değildir; o her sanal 7.1 çözümünde vardır.
- **Kulaklığa özel modda yazılmaz.** USB sürücüsü özel modu kabul ediyor ama uygulamayı yeterince sık uyandırmıyor: 3 ms periyotta saniyede 18 000, 10 ms periyotta 32 000 örnek gidiyor (48 000 gerekirken), yani ses sürekli kesiliyor. Paylaşımlı mod bunu yaşamıyor ve kulaklığa doğrudan çalan uygulamaları (Discord gibi) da susturmuyor.
- VB-CABLE ile kulaklığın saatleri tam aynı değil (~110 ppm). Kuyruk iki bloğu aşınca her beslemede bir örnek atlanır; gecikme büyümez, atlama duyulmaz. Açılıştan sonraki kesintiler `log.txt`'ye yazılır.
- Profiller `%APPDATA%\BarracudaTray\hrir` klasöründen okunur (HeSuVi 2.0.0.1 kurulum paketinin `hrir` klasörü, yalnızca 48 kHz dosyalar). Profil dosyaları depoya konmaz.

VB-CABLE kurulduğunda Windows onu stereo olarak ayarlıyor. 7.1 açılınca uygulama **CABLE Input**'u kendisi 7.1 yapar (Ses ayarlarındaki "Gelişmiş" sekmesiyle aynı işlem), elle ayar gerekmez.

### Bilinen tuzaklar

- **Dongle takılı olsa da kulaklık Bluetooth'ta olabilir.** O zaman dongle komutları kabul ediyor ama kulaklığa iletmiyor. Dongle'a geçmek için kulaklık açıkken SmartSwitch'e 2 kez basın.
- **Chroma kulaklığın içinde kapatılabiliyor.** SmartSwitch'e 3 kez basmak açıp kapatıyor. Kapalıyken bilgisayardan gönderilen hiçbir renk görünmüyor.
- **Desteklenmeyen bir özelliği sormak dongle'ı kilitliyor.** `0x20` sorgusundan sonra dongle hiçbir output report'u kabul etmedi; çıkarıp takmak gerekti. Yalnızca yukarıdaki tablodaki özellikler soruluyor; her yazma 1 saniyelik zaman aşımıyla iptal edilebiliyor.
- **Yazma onayı değer değildir.** Kulaklık bir yazmayı `özellik | 0x80` kimliğiyle ve `00` durum byte'ıyla onaylıyor; uygulama bu onayları değer olarak okumuyor, yazdıktan sonra özelliği yeniden soruyor.
- **Kulaklığı doğrudan seçen uygulamalar 7.1'i atlar.** 7.1 açıkken bütün varsayılan roller (iletişim dahil) Barracuda 7.1'e geçer, ama bir uygulamanın kendi ayarında doğrudan "Razer Barracuda X Chroma" seçiliyse sesi 7.1'den geçmeden kulaklığa gider (duyulur ama sanallaştırılmaz). Discord'da Ayarlar → Ses ve Görüntü → **Konuşmacı: Windows Varsayılanı** seçilmeli; mikrofon da Windows Varsayılanı kalmalı. `--sessions 10` hangi uygulamanın hangi cihaza çaldığını gösterir.
- **"VB-Audio Virtual Cable" asla mikrofon seçilmemeli.** Discord bu cihazı ilk gördüğünde "Sesi VB-Audio Virtual Cable olarak değiştir?" diye sorabilir; pencere X ile kapatılmalı, "Giriş + Çıkışı değiştir" mikrofonu da kabloya çevirir ve karşı taraf sizi duymaz.
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
BarracudaTray.exe --configure-cable # CABLE Input'u 7.1 yap
BarracudaTray.exe --exit            # çalışan uygulamayı düzgün kapat
BarracudaTray.exe --get 13          # vendor özelliği sor (yalnızca tablodakiler)
BarracudaTray.exe --set 13 07       # vendor özelliği yaz (yalnızca tablodakiler)
BarracudaTray.exe --devices         # ses cihazları, formatları ve özel modda desteklenenler
BarracudaTray.exe --meters 10       # hangi ses cihazından ses geçtiğini 10 sn göster
BarracudaTray.exe --sessions 10     # hangi uygulama hangi cihaza çalıyor, ne kadar yüksek
BarracudaTray.exe --surround 5 razer tone  # CABLE'a test tonu çalıp motorun giriş/çıkış seviyesini ölç
BarracudaTray.exe --selftest razer  # konvolüsyonu bir profile karşı doğrula
BarracudaTray.exe --surround 10     # 7.1 motorunu 10 sn çalıştır, gecikme ve kulaklığa giden örnek/sn dökümünü yazdır
BarracudaTray.exe --bench razer     # konvolüsyonun tek başına ne kadar işlemci kullandığı
```

## Derleme

.NET 10 SDK gerekir.

```
dotnet build -c Release
```
