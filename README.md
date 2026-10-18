# MuyaAI

Kişisel kullanım için yerel ASP.NET Core arayüzü, Ollama sohbet modeli, PDF sohbeti ve isteğe bağlı internet araması. Genel web araması SearXNG, hava durumu sorguları anahtarsız Open-Meteo API'sini kullanır.

## Gerekenler

- .NET 10 SDK
- Ollama çalışır durumda
- Docker Desktop kurulu ve açık (internet araması için)

Ollama'nın model ayarları `MyAI.Api/appsettings.json` içindeki `Ollama` bölümünden gelir. Yapılandırılmış modeller kurulu değilse:

```sh
ollama pull gemma4:e2b
ollama pull embeddinggemma
```

## İlk kurulum ve çalıştırma

Terminalde proje klasörüne geç:

```sh
cd /Users/muya/Projects/MyAI
```

Anahtarsız yerel arama servisini başlat:

```sh
docker compose up -d
```

Ardından uygulamayı çalıştır:

```sh
dotnet run --project MyAI.Api
```

Terminalde gösterilen yerel adresi tarayıcıda aç. MuyaAI'de **İnternette ara** seçeneği açıkken SearXNG arama sonuçlarını yerel Ollama modeline bağlam olarak verir ve cevapta kaynak bağlantılarını gösterir.

SearXNG açık kaynaklıdır ve API anahtarı istemez. Genel arama sorgusu bu bilgisayardaki SearXNG'e gider; SearXNG sonuç bulmak için sorguyu seçili internet arama motorlarına iletir. Hava durumu sorularında uygulama konumu bulur, Open-Meteo'dan güncel model ve günlük tahmin değerlerini alır, sayıları doğrudan gösterir; böylece modelin farklı günleri tek cevapta birleştirmesi önlenir. Open-Meteo ücretsiz servisi kişisel, ticari olmayan kullanım içindir ve hava verisi lisans gereği kaynak gösterilerek sunulur.

Uygulamayı kapatmak için uygulamanın çalıştığı terminalde `Ctrl+C` tuşlarına bas. Arama servisini durdurmak için proje klasöründe:

```sh
docker compose down
```

Bilgi tabanına yüklenen dosyaların çıkarılmış metinleri `MyAI.Api/data/knowledge.json` dosyasında saklanır. Bu dosya `.gitignore` kapsamındadır ve yalnızca bu bilgisayardaki bilgi tabanı için kullanılır.

Sohbetler de `MyAI.Api/data/conversations.json` dosyasında bu bilgisayarda saklanır. Arayüzün solundaki **Sohbetler** listesinden geçmiş konuşmaları yeniden açabilir veya silebilirsin. Tarayıcı yenilense ya da uygulama yeniden başlatılsa da sohbet geçmişi korunur.
