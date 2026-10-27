# Barracuda Tray

Razer Barracuda X Chroma için hafif bir Windows tray uygulaması. Razer Synapse, Razer Audio ve 7.1 uygulamalarının yerine geçmesi hedefleniyor.

## Durum

| Faz | İçerik | Durum |
|---|---|---|
| 1 | RGB: aç/kapat, renk, parlaklık, Nefes / Gökkuşağı efektleri | ✅ |
| 2 | 7.1 sanal surround (kendi motoru + HeSuVi profilleri + VB-CABLE) | 🟡 yazıldı, uçtan uca test bekliyor |
| 3 | Pil, şarj, mikrofon durumu | 🟡 menüde gösteriliyor, bildirimler yok |
| 4 | EQ, sidetone, Rahatsız Etmeyin, güç tasarrufu | ⏳ |
| 5 | Profiller, Windows ile başlama, kısayollar | ⏳ |

## Nasıl çalışır

Kulaklığın ışıkları standart **HID LampArray** (usage page `0x59`) arayüzüyle kontrol ediliyor. Uygulama feature report'ları doğrudan dongle'a (`1532:0574`) gönderir. Bu yüzden ne Razer yazılımına ne de Windows Dinamik Aydınlatma'ya ihtiyaç var.

- Renkler `LampMultiUpdateReport` (`0x34`) ile gönderiliyor. Bu firmware `LampRangeUpdateReport`'u (`0x35`) yok sayıyor.
- Kulaklık kapanıp açıldığında renkleri unuttuğu için sabit renk her 5 saniyede bir yeniden gönderiliyor. Efektler 25 fps çalışıyor.
- Ayarlar `%APPDATA%\BarracudaTray\settings.json` dosyasında saklanıyor.

- Feature report'lar kendi gerçek uzunluklarında gönderiliyor. Windows normalde hepsini en uzun raporun boyuna (51 byte) dolduruyor.
- Bağlantı, pil, şarj, mikrofon ve Chroma anahtarı durumu Razer'ın vendor kanalından (usage page `0xFF14`, rapor `0x02`) okunuyor. Kulaklık bu olayları kendiliğinden gönderiyor; uygulama açılışta yalnızca bir kez pil seviyesini soruyor (`0x21`).

### 7.1 sanal surround

Kulaklığın ses cihazı Windows'ta yalnızca stereo olduğu için oyunlar ona 7.1 gönderemez. Razer bunu kendi sanal 7.1 cihazı ve servisiyle çözüyor; bu uygulama da aynı yolu açık parçalarla izliyor:

```
oyun ─7.1─► CABLE Input ══ VB-CABLE ══► CABLE Output ─► motor ─stereo─► kulaklık
            (varsayılan cihaz)                          (HeSuVi profili)
```

- 7.1 açılınca varsayılan ses cihazı **CABLE Input** yapılır; kapanınca ya da uygulamadan çıkınca kulaklığa geri verilir. İletişim cihazına dokunulmaz, sesli sohbet uygulamaları kulaklıkta kalır.
- Motor, 7.1 sesi HeSuVi profiliyle iki kulağa çevirir. Kanal düzeni ve karıştırma kuralları HeSuVi'nin kaynak kodundan alındı: LFE 111 Hz'de kesilip merkeze eklenir, 7 kanallı profillerde sağ taraf solun aynasıdır. Equalizer APO gerekmez.
- Gecikme için: 128 örneklik bölümlenmiş FFT konvolüsyonu (2,7 ms), iki kulak tek ters FFT'de; VB-CABLE `IAudioClient3` ile en küçük periyotta okunur; kulaklığa **özel modda** (Windows karıştırıcısını atlayarak) yazılır, olmazsa paylaşımlı moda düşülür. Menüdeki gecikme bu uygulamanın eklediği kısmın tahminidir; oyunun CABLE'a yazarkenki Windows gecikmesi dahil değildir.
- Profiller `%APPDATA%\BarracudaTray\hrir` klasöründen okunur (HeSuVi 2.0.0.1 kurulum paketinin `hrir` klasörü, yalnızca 48 kHz dosyalar). Profil dosyaları depoya konmaz.

Kurulum: [VB-CABLE](https://vb-audio.com/Cable/)'ı yönetici olarak kurup bilgisayarı yeniden başlatın, sonra Ses ayarları → Kayıttan yürütme → **CABLE Input** → Yapılandır → **7.1 Surround** seçin.

### Bilinen tuzaklar

- **Dongle takılı olsa da kulaklık Bluetooth'ta olabilir.** O zaman dongle komutları kabul ediyor ama kulaklığa iletmiyor. Dongle'a geçmek için kulaklık açıkken SmartSwitch'e 2 kez basın.
- **Chroma kulaklığın içinde kapatılabiliyor.** SmartSwitch'e 3 kez basmak açıp kapatıyor. Kapalıyken bilgisayardan gönderilen hiçbir renk görünmüyor.
- **Desteklenmeyen bir özelliği sormak dongle'ı kilitliyor.** `0x20` sorgusundan sonra dongle hiçbir output report'u kabul etmedi; çıkarıp takmak gerekti. Sadece `0x21` (pil) ve `0x2A` (şarj) sorgularının çalıştığı biliniyor. Yazma işlemleri zaman aşımıyla iptal edilebiliyor.
- **Razer Synapse arka planda çalışıyorsa** ışığı o da yönetir. Razer uygulamalarını kapatın veya kaldırın.
- **Windows Dinamik Aydınlatma** bu cihazda açıksa çakışabilir. Ayarlar → Kişiselleştirme → Dinamik Aydınlatma bölümünden kulaklık için kapatın.

## Kullanım

Tray simgesine sağ tıklayınca menü açılır. Çift tıklamak ışıkları açar/kapatır.

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
BarracudaTray.exe --get 21          # vendor özelliği sor (yalnızca 21 ve 2A güvenli)
BarracudaTray.exe --devices         # ses cihazları ve formatları
BarracudaTray.exe --selftest razer  # konvolüsyonu bir profile karşı doğrula
BarracudaTray.exe --surround 10     # 7.1 motorunu 10 sn çalıştır, gecikmeyi yazdır (shared: paylaşımlı mod)
```

## Derleme

.NET 10 SDK gerekir.

```
dotnet build -c Release
```
