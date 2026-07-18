# Türkçe Rehber

**`master`'a merge et → kapalı sunucun kendini güncellesin.** GitHub imajı
bulutta derler; sunucundaki küçük bir self-hosted runner imajı çeker ve
compose projesini yeniden başlatır. Yalnızca dışa bağlantı — açık port yok,
SSH yok, sunucuda git token'ı yok.

## Hızlı kurulum (Web UI ile)

```bash
sudo curl -fsSL -o /usr/local/bin/pinqops-ui \
  https://github.com/pinqponq/pinqops/releases/latest/download/pinqops-ui
sudo chmod +x /usr/local/bin/pinqops-ui

sudo pinqops-ui install-service
sudo journalctl -u pinqops-ui | grep "setup code"
```

1. `http://<sunucu>:7467` adresini aç, konsoldaki **kurulum kodunu** gir,
   panel şifreni oluştur.
2. Kenar çubuğundaki **GitHub** menüsüne gir (bağlanana kadar kilit ikonu
   taşır). **GitHub ile giriş yap** — github.com'da kısa bir kodu onaylarsın
   (veya token yapıştır).
3. Depolarında ara, birini seç, **Kur**'a bas.

## Kurulum sihirbazı ne yapar?

| Adım | Açıklama |
|---|---|
| Depo bağlanıyor | Seçilen depo dağıtım hedefi olarak kaydedilir |
| Dockerfile | Sadece kontrol edilir — yoksa uyarı verir (uygulama kodunu sihirbaz yazamaz) |
| Deploy workflow | Eksikse `.github/workflows/deploy.yml` depoya commit'lenir |
| Compose projesi | Eksikse deponun imajı için `/opt/pinqops/docker-compose.yml` üretilir |
| Self-hosted runner | İndirilir, **bu depoya** kaydedilir, systemd servisi kurulur. Başka bir depoya kayıtlı eski bir runner varsa önce düzgünce sökülür |
| Doğrulama | Runner'ın GitHub'da gerçekten göründüğü teyit edilir |

Sihirbaz bittiğinde tek yapman gereken: bir PR'ı `master`'a merge etmek.
Gerisi otomatik.

## Uygulama kataloğu

**Uygulamalar** menüsünde ~50 hazır uygulama var (Redis, PostgreSQL,
Grafana, MinIO, n8n, …). **Kur**'a bas, istersen portları değiştir — kurulum
arka planda yürür ve "imaj çekiliyor → başlatılıyor" ilerlemesi sayfa
yenilemeden görünür. Veriler adlandırılmış volume'larda tutulur; **Kaldır**
konteyneri siler, veriyi korur.

## Loglar

Konteyner logları ayrı bir menü değil: **Konteynerler** görünümünde bir
konteynerin *loglar* düğmesine bas — panel aynı sayfada açılır (takip et /
indir / kapat).

## Sık sorunlar

- **Runner GitHub'da görünmüyor:** eski sürümlerde başka depoya kayıtlı
  runner "kurulu" sanılıyordu; artık sihirbaz uyuşmazlığı görür, eskisini
  söker ve doğrusunu kaydeder. Sihirbazı yeniden çalıştırman yeterli.
- **Runner kurulu ama çevrimdışı:** Runner sayfasından **Servisi başlat**,
  ya da `cd /opt/actions-runner && sudo ./svc.sh start`.
- **Runner listelenemiyor (token hatası):** token'da *Administration: read*
  yoksa panel o satırı "durum bilinmiyor" olarak gösterir; dağıtım yine
  çalışır.

Ayrıntılar (İngilizce):
[SETUP](https://github.com/pinqponq/pinqops/blob/master/docs/SETUP.md) ·
[TOKENS](https://github.com/pinqponq/pinqops/blob/master/docs/TOKENS.md) ·
[SECURITY](https://github.com/pinqponq/pinqops/blob/master/SECURITY.md)
