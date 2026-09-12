# All Voice Commands by Language

Quick-reference utterances from the interaction model files, grouped by language: up to 6 samples per intent (first of each distinct slot shape, then model order), emitted by `scripts/generate_voice_reference.py` (the same run writes docs/VOICE_COMMANDS_BY_LOCALE.md, the complete per-locale reference). Do not hand-edit the locale tables; edit the models and re-run the generator.

[← Back to README](README.md)

### Language Index

- **[Arabic](#arabic)**: [ar-SA](#ar-sa)
- **[Dutch](#dutch)**: [nl-NL](#nl-nl)
- **[English](#english)**: [en-AU](#en-au), [en-CA](#en-ca), [en-GB](#en-gb), [en-IN](#en-in), [en-US](#en-us)
- **[French](#french)**: [fr-CA](#fr-ca), [fr-FR](#fr-fr)
- **[German](#german)**: [de-DE](#de-de)
- **[Hindi](#hindi)**: [hi-IN](#hi-in)
- **[Italian](#italian)**: [it-IT](#it-it)
- **[Japanese](#japanese)**: [ja-JP](#ja-jp)
- **[Portuguese](#portuguese)**: [pt-BR](#pt-br)
- **[Spanish](#spanish)**: [es-ES](#es-es), [es-MX](#es-mx), [es-US](#es-us)

### <a id="ar-sa"></a>Arabic (ar-SA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `أضف {song} إلى قائمة الانتظار` · `أضف {song} لـ {musician} إلى قائمة الانتظار` · `ضع {song} في قائمة الانتظار` · `أضف {song} إلى القائمة` |
| Browse Library | `{browse_category}` · `تصفح {browse_category}` · `أرني {browse_category}` · `أعرض {browse_category}` · `ما {browse_category} لدي` · `ما {browse_category} الموجودة` |
| Clear Queue | `امسح قائمة الانتظار` · `أفرغ قائمة الانتظار` · `أزل كل شيء من قائمة الانتظار` |
| Continue Watching | `أكمل المشاهدة` · `أكمل الاستماع` · `أكمل من حيث توقفت` · `ما كنت أشاهده` · `أكمل` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `تابعني` · `استمر في التشغيل` · `انقل التشغيل` |
| Go To Chapter | `الفصل التالي` · `اذهب إلى الفصل {chapter_number}` · `الفصل السابق` · `انتقل إلى الفصل {chapter_number}` · `تخطى فصلاً` |
| In Progress Media List | `ماذا كنت أستمع` · `ماذا كنت أشاهد` · `ما الذي قيد التقدم` · `أظهر تقدمي` · `ما كنت ألعب` · `ما الذي بدأته` |
| Learn My Voice | `تعلم صوتي` · `تذكر صوتي` · `تعرف علي` · `اربط صوتي` · `هذا صوتي` · `اضبط ملفي الصوتي` |
| List Queue | `ماذا في قائمة الانتظار` · `ماذا يأتي بعد ذلك` · `أظهر قائمة الانتظار` · `ماذا سيشغل بعد ذلك` |
| Loop Song On | `كرر هذه الأغنية` · `كرر هذه الأغنية دائماً` · `أعد الأغنية` · `أعد هذه الأغنية` · `أعد هذه الأغنية دائماً` |
| Mark Favorite | `أعجبني هذا` · `أعجبني الفيديو` · `أعجبني الأغنية` · `أعجبني الموسيقى` · `أضف الفيديو إلى المفضلة` · `أضف الأغنية إلى المفضلة` |
| Media Info | `ما اسم الأغنية` · `ما {media_info_type} هذا` · `ما اسم الفيديو` · `ما الذي يشغل الآن` · `أخبرني عن {media_info_type}` · `من يغني هذا` |
| Play Album | `شغل الألبوم {album}` · `شغل الألبوم {album} لـ {musician}` · `شغل ألبوم {musician}` · `شغل ألبوم {album}` · `شغل ألبوم {album} لـ {musician}` · `استمع إلى الألبوم {album}` |
| Play Artist Songs | `شغل أغاني {musician}` · `شغل موسيقى {musician}` · `شغل أغانٍ لـ {musician}` · `استمع إلى {musician}` · `استمع إلى أغاني {musician}` · `استمع إلى موسيقى {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `شغل أغانٍ من {decade}` · `شغل {genre} من {decade}` · `شغل أغاني {decade}` · `شغل موسيقى {decade}` · `شغل أفضل {decade}` · `أريد سماع موسيقى {decade}` |
| Play By Genre | `شغل موسيقى {genre}` · `شغل أغاني {genre}` · `شغل {genre}` · `أريد الاستماع إلى {genre}` · `أعطني موسيقى {genre}` · `هل يمكنك تشغيل {genre}` |
| Play Channel | `شغل القناة {channel}` · `شغل الراديو {channel}` |
| Play Episode | `شغل الموسم {season_number} الحلقة {episode_number} من {series_name}` · `شغل {series_name} الموسم {season_number} الحلقة {episode_number}` · `شاهد الموسم {season_number} الحلقة {episode_number} من {series_name}` · `شاهد {series_name} الموسم {season_number} الحلقة {episode_number}` |
| Play Favorites | `شغل مفضلاتي` · `شغل {media_type} المفضلة` · `شغل مفضلات {username}` · `شغل {media_type} المفضلة لـ {username}` · `شغل أغاني المفضلة` · `استمع إلى مفضلات {username}` |
| Play Last Added | `شغل آخر ما أضيف من {media_type}` · `شغل {media_type} المضافة {time_period}` · `شغل آخر ما أضيف من أغاني` · `شغل ما أضيف مؤخراً من {media_type}` · `شغل {media_type} جديدة` · `شغل ما أضيف مؤخراً` |
| Play Mood Music | `شغل موسيقى {mood}` · `شغل {mood}` · `شغل أغانٍ {mood}` · `أريد موسيقى {mood}` |
| Play Next | `شغل {song} بعد ذلك` · `شغل {song} لـ {musician} بعد ذلك` · `أريد سماع {song} بعد ذلك` · `شغل {song} بعد هذا` |
| Play Next Episode | `شغل الحلقة التالية من {series_name}` · `شغل أحدث حلقة من {series_name}` · `أكمل مشاهدة {series_name}` |
| Play Playlist | `شغل قائمة التشغيل {playlist}` · `شغل قائمة تشغيلي {playlist}` · `ابدأ قائمة التشغيل {playlist}` · `استمع إلى قائمة التشغيل {playlist}` · `هل يمكنك تشغيل قائمة التشغيل {playlist}` · `أريد سماع قائمة التشغيل {playlist}` |
| Play Podcast | `شغل البودكاست {podcast_name}` · `استمع إلى البودكاست {podcast_name}` · `شغل آخر حلقة من {podcast_name}` · `شغل أحدث حلقة من {podcast_name}` · `ابدأ البودكاست {podcast_name}` |
| Play Radio | `شغل الراديو` · `شغل محطة الراديو {station}` · `شغل وضع الراديو` · `ابدأ الراديو` · `شغل موسيقى مشابهة` · `شغل أغانٍ مشابهة` |
| Play Random | `شغل {media_type} عشوائي` · `شغل شيء عشوائي` · `شغل {media_type} عشوائي من {genre}` · `شغل {genre} عشوائي` · `اخلط {media_type}` · `شغل أغانٍ عشوائية` |
| Play Song | `شغل {song}` · `شغل {song} لـ {musician}` · `شغل الأغنية {song}` · `شغل أغنية {song}` · `شغل الأغنية {song} لـ {musician}` · `شغل أغنية {song} لـ {musician}` |
| Play Video | `شغل الفيديو {title}` · `شغل {title}` · `شاهد {title}` · `هل يمكنك تشغيل {title}` · `أريد مشاهدة {title}` · `شغل الفيلم {title}` |
| Query Artist Library | `ما الأغاني لدينا لـ {musician}` · `ما {query_type} لدينا لـ {musician}` · `ما الألبومات لدينا لـ {musician}` · `ما الذي لدينا لـ {musician}` · `أعرض الأغاني لـ {musician}` · `أعرض الألبومات لـ {musician}` |
| Query Recently Added | `ما الجديد` · `ما الذي أضيف مؤخراً` · `ما الجديد في مكتبتي` · `أرني ما أضيف مؤخراً` · `هل هناك شيء جديد` · `ما الذي تمت إضافته مؤخراً` |
| Recommend | `أوصني بشيء` · `اقترح {media_type}` · `أوصني بموسيقى` · `أوصني بفيلم` · `اقترح شيئاً لمشاهدته` · `شغل شيئاً قد يعجبني` |
| Search Media | `ابحث عن فيلم {query}` · `ابحث عن محتوى {query}` · `ابحث عن فيديو {query}` · `ابحث عن مسلسل {query}` · `جد فيلم {query}` · `جد محتوى {query}` |
| Set Reminder | `ذكرني بعد {duration_minutes} دقيقة` · `ذكرني الساعة {reminder_time}` · `اضبط منبها بعد {duration_minutes} دقيقة` · `اضبط منبها الساعة {reminder_time}` |
| Show More | `أظهر المزيد` · `المزيد` · `الصفحة التالية` · `استمر` · `ماذا أيضا` · `المزيد من النتائج` |
| Shuffle Play | `شغل قائمة التشغيل {playlist} بشكل عشوائي` · `اخلط قائمة التشغيل {playlist}` · `شغل قائمة التشغيل {playlist} في وضع الخلط` |
| Sleep Timer | `أوقف التشغيل بعد {duration_minutes} دقيقة` · `اضبط مؤقت النوم لـ {duration_minutes} دقيقة` · `مؤقت نوم {duration_minutes} دقيقة` · `أوقف بعد {duration_minutes} دقيقة` |
| Turn Radio Off | `أوقف وضع الراديو` · `عطّل وضع الراديو` · `وضع الراديو متوقف` · `أوقف الراديو` |
| Turn Radio On | `شغل وضع الراديو` · `فعّل وضع الراديو` · `وضع الراديو قيد التشغيل` · `شغل الراديو` |
| Unmark Favorite | `لم يعجبني هذا` · `لم يعجبني الفيديو` · `لم يعجبني الأغنية` · `أزل الفيديو من المفضلة` · `أزل الأغنية من المفضلة` |
| Who Am I | `من أنا` · `أي حساب هذا` · `ما الحساب الذي أستخدمه` · `من يتحدث` · `أي ملف نشط` |

### <a id="de-de"></a>German (de-DE)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Füge {song} zur Wiedergabeliste hinzu` · `Füge {song} von {musician} zur Wiedergabeliste hinzu` · `Setze {song} auf die Warteschlange` · `Setze {song} von {musician} auf die Warteschlange` · `Stelle {song} hinten an` · `Füge {song} hinzu` |
| Browse Library | `{browse_category}` · `nur {browse_category}` · `ich möchte {browse_category}` · `durchsuche {browse_category}` · `zeige mir {browse_category}` · `liste {browse_category}` |
| Clear Queue | `Lösche meine Warteschlange` · `Lösche die Warteschlange` · `Leere meine Warteschlange` · `Leere die Warteschlange` · `Entferne alles aus der Warteschlange` · `Lösche meine Playlist` |
| Continue Watching | `Weiter schauen` · `Weiter hören` · `Mach da weiter wo ich war` · `Weiter` |
| Find Song | `finde ein lied` · `finde ein lied namens {titleKeywords}` · `hilf mir ein lied zu finden` · `suche ein lied` · `ich suche ein lied` · `suche ein lied namens {titleKeywords}` |
| Find Song By Artist | `finde ein lied von {musician}` · `hilf mir ein lied zu finden von {musician}` · `suche ein lied von {musician}` · `ich suche ein lied von {musician}` |
| Follow Me | `folge mir` · `weiterhören` · `Wiedergabe fortsetzen` · `Wiedergabe übernehmen` · `weiter abspielen` · `musik mitnehmen` |
| Go To Chapter | `Nächstes Kapitel` · `Gehe zu Kapitel {chapter_number}` · `Vorheriges Kapitel` · `Springe zu Kapitel {chapter_number}` · `Ein Kapitel vor` · `Ein Kapitel zurück` |
| In Progress Media List | `was höre ich gerade` · `was schaue ich gerade` · `was ist in bearbeitung` · `zeige meinen fortschritt` · `was habe ich angefangen` |
| Learn My Voice | `Lerne meine Stimme` · `Erkenne meine Stimme` · `Verknüpfe meine Stimme` · `Das ist meine Stimme` · `Erkenne mich` · `Richte mein Stimmprofil ein` |
| List Queue | `Was ist in meiner Warteschlange` · `Was steht als Nächstes an` · `Was kommt als Nächstes` · `Zeige meine Warteschlange` · `Zeige die Warteschlange` · `Was wird als Nächstes gespielt` |
| Loop All Off | `Wiederholung aus` · `Schleife aus` |
| Loop All On | `Wiederholung an` · `Schleife an` |
| Loop Song On | `Wiederhole dieses Lied` · `Wiederhole diesen Song` · `Wiederhole diesen Titel` · `Loop dieses Lied` · `Wiederhole endlos` · `Spiele dieses Lied in Schleife` |
| Mark Favorite | `Das gefaellt mir` · `Das Video gefaellt mir` · `Das Lied gefaellt mir` · `Die Musik gefaellt mir` · `Fuege das Video zu meinen Favoriten hinzu` · `Fuege das Lied zu meinen Favoriten hinzu` |
| Media Info | `Was ist der {media_info_type}` · `Was läuft gerade` · `Was ist die {media_info_type}` · `Sag mir den {media_info_type}` · `Sag mir die {media_info_type}` · `Welcher {media_info_type} ist das` |
| Play Album | `Spiele das Album {album}` · `Spiele das Album {album} von {musician}` · `ein Album von {musician}` · `Spiele Album {album}` · `Spiele Album {album} von {musician}` · `Höre das Album {album}` |
| Play Artist Songs | `Spiele Lieder von {musician}` · `Spiele Musik von {musician}` · `Spiele Titel von {musician}` · `Spiele Songs von {musician}` · `Spiele {musician}` · `Spiele etwas von {musician}` |
| Play Book | `spiele {book}` · `spiele das buch {book}` · `spiele hörbuch {book}` · `hör {book}` · `hör das buch {book}` |
| Play By Decade | `Spiele Lieder aus den {decade}` · `Spiele {genre} aus den {decade}` · `Spiele Titel aus den {decade}` · `Spiele Hits aus den {decade}` · `Spiele Musik aus den {decade}` · `Spiele {decade} Hits` |
| Play By Genre | `Spiele {genre} Musik` · `Spiele {genre}` · `Spiele etwas {genre}` · `Ich möchte {genre} hören` · `Spiele mir {genre} Musik` |
| Play Channel | `Kanal {channel}` · `Spiele Radio {channel}` · `Radio {channel}` |
| Play Episode | `spiele staffel {season_number} folge {episode_number} von {series_name}` · `spiele {series_name} staffel {season_number} folge {episode_number}` · `schau staffel {season_number} folge {episode_number} von {series_name}` · `Zu spielen staffel {season_number} folge {episode_number} von {series_name}` · `Zu schauen staffel {season_number} folge {episode_number} von {series_name}` |
| Play Favorites | `Spiele meine Lieblings {media_type}` · `Spiele meine Favoriten` · `spiele favoriten von {username}` · `spiele lieblings {media_type} von {username}` · `Spiele meine {media_type} Favoriten` · `spiele die favoriten von {username}` |
| Play Last Added | `Spiele neu hinzugefuegte {media_type}` · `Spiele neue Medien` · `Spiele kuerzlich hinzugefuegte {media_type}` |
| Play Mood Music | `spiele {mood} musik` · `spiele etwas {mood}` · `ich möchte {mood} musik` · `spiele mir etwas {mood}` |
| Play Next | `Spiele {song} als Nächstes` · `Spiele {song} von {musician} als Nächstes` · `Spiele {song} danach` · `Spiele {song} von {musician} danach` · `Ich möchte {song} als Nächstes hören` · `Setze {song} als Nächstes` |
| Play Next Episode | `spiele die nächste folge von {series_name}` · `spiele die nächste folge der serie {series_name}` · `schau die nächste folge von {series_name}` · `spiele die neueste folge von {series_name}` · `schau {series_name} weiter` · `Zu spielen die nächste folge von {series_name}` |
| Play Playlist | `Spiele die Playlist {playlist}` · `Spiele meine Playlist {playlist}` · `Spiele Playlist {playlist}` · `Playlist {playlist} abspielen` · `Die Playlist {playlist} abspielen` · `Meine Playlist {playlist} abspielen` |
| Play Podcast | `Spiele den Podcast {podcast_name}` · `Spiele Podcast {podcast_name}` · `Höre den Podcast {podcast_name}` · `Höre Podcast {podcast_name}` · `Spiele die neueste Folge von {podcast_name}` · `Starte den Podcast {podcast_name}` |
| Play Radio | `Spiele Radio` · `Spiele den Radiosender {station}` · `Starte Radio` · `Spiele den Radiomodus` · `Spiele ähnliche Musik` · `Spiele ähnliche Lieder` |
| Play Random | `Spiele zufällige {media_type}` · `Spiele etwas zufälliges` · `Spiele zufällige {media_type} aus {genre}` · `Spiele eine zufällige {media_type}` · `Zufällige {media_type} abspielen` · `Überrasche mich mit {media_type}` |
| Play Song | `Spiele {song}` · `Spiele {song} von {musician}` · `Spiele das Lied {song}` · `Spiele Lied {song}` · `Spiele das Lied {song} von {musician}` · `Spiele Lied {song} von {musician}` |
| Play Video | `Spiele das Video {title}` · `Ich möchte {title} sehen` · `Lass uns {title} schauen` · `Ich will {title} anschauen` · `Zeig mir {title}` · `Kannst du {title} zeigen` |
| Query Artist Library | `Welche Titel haben wir von {musician}` · `Welche {query_type} haben wir von {musician}` · `Welche Lieder haben wir von {musician}` · `Welche Alben haben wir von {musician}` · `Was haben wir von {musician}` · `Zeige Titel von {musician}` |
| Query Recently Added | `was ist neu` · `was wurde kürzlich hinzugefügt` · `zeige mir die Neuzugänge` · `gibt es etwas Neues` · `was ist neu in meiner Bibliothek` · `die neuesten Elemente` |
| Recommend | `empfehle etwas` · `empfehle {media_type}` · `empfehle musik` · `empfehle einen film` · `schlage etwas vor` · `spiele etwas das mir gefällt` |
| Repeat Single On | `Lied wiederholen` · `Titel wiederholen` · `Video wiederholen` · `Das wiederholen` |
| Search Media | `Suche nach einem Film {query}` · `Suche nach einem Video {query}` · `Suche nach einer Serie {query}` · `Suche nach Inhalt {query}` · `Finde einen Film {query}` · `Finde einen Inhalt {query}` |
| Set Reminder | `erinnere mich in {duration_minutes} minuten` · `erinnere mich um {reminder_time}` · `stelle eine erinnerung für {duration_minutes} minuten` · `setze eine erinnerung auf {reminder_time}` |
| Show More | `zeig mehr` · `noch mehr` · `weiter` · `nächste seite` · `mehr ergebnisse` · `was gibt es noch` |
| Shuffle Play | `spiele die Playlist {playlist} in zufälliger Reihenfolge` · `mische die Playlist {playlist}` · `spiele die Playlist {playlist} im Zufallsmodus` |
| Sleep Timer | `stoppe in {duration_minutes} minuten` · `schlaf-timer {duration_minutes} minuten` · `stoppe nach {duration_minutes} minuten` · `ausschalten in {duration_minutes} minuten` |
| Turn Radio Off | `Schalte den Radiomodus aus` · `Deaktiviere den Radiomodus` · `Radiomodus aus` · `Schalte Radio aus` · `Deaktiviere Radio` · `Stoppe den Radiomodus` |
| Turn Radio On | `Schalte den Radiomodus ein` · `Aktiviere den Radiomodus` · `Radiomodus an` · `Schalte Radio ein` · `Aktiviere Radio` |
| Unmark Favorite | `Das gefaellt mir nicht` · `Das Video gefaellt mir nicht` · `Das Lied gefaellt mir nicht` · `Die Musik gefaellt mir nicht` · `Entferne das Video aus meinen Favoriten` · `Entferne das Lied aus meinen Favoriten` |
| Who Am I | `Wer bin ich` · `Welches Konto ist das` · `Welches Konto benutze ich` · `Wer spricht` · `Welches Profil ist aktiv` · `Bin ich erkannt` |

### <a id="en-au"></a>English - Australia (en-AU)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} by {musician} to my queue` · `add {song} to the queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` |
| Browse Library | `{browse_category}` · `just {browse_category}` · `I want {browse_category}` · `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `keep playing` · `pick up where I left off` |
| Go To Chapter | `Next chapter` · `Go to chapter {chapter_number}` · `Previous chapter` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat this song forever` · `repeat the song` · `repeat this song` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What {media_info_type} is this` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` |
| Play Album | `play the album {album}` · `play the album {album} by {musician}` · `an album by {musician}` · `play album {album}` · `play album {album} by {musician}` · `listen to the album {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `play songs from the {decade}` · `play {genre} from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` · `to play season {season_number} episode {episode_number} of {series_name}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my favorites` · `play favorites for {username}` · `play favorite {media_type} for {username}` · `Play my {media_type} favorite` · `play favorite songs for {username}` |
| Play Last Added | `Play last added {media_type}` · `Play new media` · `Play recently added {media_type}` · `Play newly added {media_type}` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` |
| Play Next Episode | `play the next episode of {series_name}` · `play the next episode of the series {series_name}` · `watch the next episode of {series_name}` · `play the latest episode of {series_name}` · `play the newest episode of {series_name}` · `continue watching {series_name}` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `play the playlist {playlist}` · `play my playlist {playlist}` · `start the playlist {playlist}` · `start playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` |
| Play Radio | `play radio` · `play the radio station {station}` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` |
| Play Random | `Play a random {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` |
| Play Song | `play {song}` · `play {song} by {musician}` · `play the song {song}` · `play song {song}` · `play the song {song} by {musician}` · `play song {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which {query_type} do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` |
| Recommend | `recommend something` · `recommend {media_type}` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` |
| Search Media | `Search for a movie {query}` · `Search for content {query}` · `Find a movie {query}` · `Find content {query}` · `Look for a movie {query}` · `Look for content {query}` |
| Set Reminder | `remind me in {duration_minutes} minutes` · `remind me at {reminder_time}` · `set a reminder for {duration_minutes} minutes` · `set a reminder for {reminder_time}` |
| Show More | `show more` · `next page` · `more results` · `see more` · `more` · `what else` |
| Shuffle Play | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-ca"></a>English - Canada (en-CA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} by {musician} to my queue` · `add {song} to the queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `keep playing` |
| Go To Chapter | `Next chapter` · `Go to chapter {chapter_number}` · `Previous chapter` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat this song forever` · `repeat the song` · `repeat this song` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What {media_info_type} is this` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` |
| Play Album | `play the album {album}` · `play the album {album} by {musician}` · `an album by {musician}` · `play album {album}` · `play album {album} by {musician}` · `listen to the album {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `play songs from the {decade}` · `play {genre} from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` · `to play season {season_number} episode {episode_number} of {series_name}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my favorites` · `play favorites for {username}` · `play favorite {media_type} for {username}` · `Play my {media_type} favorite` · `play favorite songs for {username}` |
| Play Last Added | `Play last added {media_type}` · `Play new media` · `Play recently added {media_type}` · `Play newly added {media_type}` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` |
| Play Next Episode | `play the next episode of {series_name}` · `play the next episode of the series {series_name}` · `watch the next episode of {series_name}` · `play the latest episode of {series_name}` · `play the newest episode of {series_name}` · `continue watching {series_name}` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `play the playlist {playlist}` · `play my playlist {playlist}` · `start the playlist {playlist}` · `start playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` |
| Play Radio | `play radio` · `play the radio station {station}` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` |
| Play Random | `Play a random {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` |
| Play Song | `play {song}` · `play {song} by {musician}` · `play the song {song}` · `play song {song}` · `play the song {song} by {musician}` · `play song {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which {query_type} do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` |
| Recommend | `recommend something` · `recommend {media_type}` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` |
| Search Media | `Search for a movie {query}` · `Search for content {query}` · `Find a movie {query}` · `Find content {query}` · `Look for a movie {query}` · `Look for content {query}` |
| Set Reminder | `remind me in {duration_minutes} minutes` · `remind me at {reminder_time}` · `set a reminder for {duration_minutes} minutes` · `set a reminder for {reminder_time}` |
| Show More | `show more` · `next page` · `more results` · `see more` · `more` · `what else` |
| Shuffle Play | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-gb"></a>English - UK (en-GB)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} by {musician} to my queue` · `add {song} to the queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` |
| Browse Library | `{browse_category}` · `just {browse_category}` · `I want {browse_category}` · `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `move playback here` · `keep playing` |
| Go To Chapter | `Next chapter` · `Go to chapter {chapter_number}` · `Previous chapter` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat this song forever` · `repeat the song` · `repeat this song` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What {media_info_type} is this` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` |
| Play Album | `play the album {album}` · `play the album {album} by {musician}` · `an album by {musician}` · `play album {album}` · `play album {album} by {musician}` · `listen to the album {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `play songs from the {decade}` · `play {genre} from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` · `to play season {season_number} episode {episode_number} of {series_name}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my favorites` · `play favourites for {username}` · `play favourite {media_type} for {username}` · `Play my {media_type} favorite` · `Play my favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play new media` · `Play recently added {media_type}` · `Play newly added {media_type}` · `what's new in {media_type}` · `play the latest {media_type}` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` |
| Play Next Episode | `play the next episode of {series_name}` · `play the next episode of the series {series_name}` · `watch the next episode of {series_name}` · `play the latest episode of {series_name}` · `play the newest episode of {series_name}` · `continue watching {series_name}` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `put on the playlist {playlist}` · `put on my playlist {playlist}` · `start the playlist {playlist}` · `start my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` |
| Play Radio | `play radio` · `play the radio station {station}` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` |
| Play Random | `Play a random {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` |
| Play Song | `play {song}` · `play {song} by {musician}` · `play the song {song}` · `play song {song}` · `play the song {song} by {musician}` · `play song {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which {query_type} do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` |
| Recommend | `recommend something` · `recommend {media_type}` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` |
| Search Media | `Search for a movie {query}` · `Search for content {query}` · `Find a movie {query}` · `Find content {query}` · `Look for a movie {query}` · `Look for content {query}` |
| Set Reminder | `remind me in {duration_minutes} minutes` · `remind me at {reminder_time}` · `set a reminder for {duration_minutes} minutes` · `set a reminder for {reminder_time}` |
| Show More | `show more` · `next page` · `more results` · `see more` · `more` · `what else` |
| Shuffle Play | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-in"></a>English - India (en-IN)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} by {musician} to my queue` · `add {song} to the queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` |
| Browse Library | `{browse_category}` · `just {browse_category}` · `I want {browse_category}` · `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` |
| Go To Chapter | `Next chapter` · `Go to chapter {chapter_number}` · `Previous chapter` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat this song forever` · `repeat the song` · `repeat this song` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What {media_info_type} is this` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` |
| Play Album | `play the album {album}` · `play the album {album} by {musician}` · `an album by {musician}` · `play album {album}` · `play album {album} by {musician}` · `listen to the album {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `play songs from the {decade}` · `play {genre} from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` · `to play season {season_number} episode {episode_number} of {series_name}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my favorites` · `play favorites for {username}` · `play favorite {media_type} for {username}` · `Play my {media_type} favorite` · `play favorite songs for {username}` |
| Play Last Added | `Play last added {media_type}` · `Play new media` · `Play recently added {media_type}` · `Play newly added {media_type}` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` |
| Play Next Episode | `play the next episode of {series_name}` · `play the next episode of the series {series_name}` · `watch the next episode of {series_name}` · `play the latest episode of {series_name}` · `play the newest episode of {series_name}` · `continue watching {series_name}` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `play the playlist {playlist}` · `play my playlist {playlist}` · `start the playlist {playlist}` · `to play the playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` |
| Play Radio | `play radio` · `play the radio station {station}` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` |
| Play Random | `Play a random {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` |
| Play Song | `play {song}` · `play {song} by {musician}` · `play the song {song}` · `play song {song}` · `play the song {song} by {musician}` · `play song {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which {query_type} do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` |
| Recommend | `recommend something` · `recommend {media_type}` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` |
| Search Media | `Search for a movie {query}` · `Search for content {query}` · `Find a movie {query}` · `Find content {query}` · `Look for a movie {query}` · `Look for content {query}` |
| Set Reminder | `remind me in {duration_minutes} minutes` · `remind me at {reminder_time}` · `set a reminder for {duration_minutes} minutes` · `set a reminder for {reminder_time}` |
| Show More | `show more` · `next page` · `more results` · `see more` · `more` · `what else` |
| Shuffle Play | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-us"></a>English - US (en-US)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} by {musician} to my queue` · `add {song} to the queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` |
| Browse Library | `{browse_category}` · `just {browse_category}` · `I want {browse_category}` · `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `move playback here` · `keep playing` |
| Go To Chapter | `Next chapter` · `Go to chapter {chapter_number}` · `Previous chapter` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` |
| Jump To Position | `jump to {position_hours} hour {position_minutes} minutes` · `jump to {position_minutes} minutes` · `skip to {position_hours} hours` · `jump to {position_hours} hours {position_minutes} minutes {position_seconds} seconds` · `jump to {position_minutes} minutes {position_seconds} seconds` · `go to {position_hours} hour {position_minutes} minutes` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat this song forever` · `repeat the song` · `repeat this song` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `I like this song` · `I like this` |
| Media Info | `what's on` · `What {media_info_type} is this` · `information about this` · `what is this` · `details about this` · `tell me about this` |
| Play Album | `play the album {album}` · `play the album {album} by {musician}` · `an album by {musician}` · `play album {album}` · `play album {album} by {musician}` · `listen to the album {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `play songs from the {decade}` · `play {genre} from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` · `to play season {season_number} episode {episode_number} of {series_name}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my favorites` · `play favorites for {username}` · `play favorite {media_type} for {username}` · `Play my {media_type} favorite` · `Play my favorite songs` |
| Play Last Added | `Play last added {media_type}` · `Play new media` · `Play {media_type} added {time_period}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play something new` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` |
| Play Next Episode | `play the next episode of {series_name}` · `play the next episode of the series {series_name}` · `watch the next episode of {series_name}` · `play the latest episode of {series_name}` · `play the newest episode of {series_name}` · `continue watching {series_name}` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `put on the playlist {playlist}` · `put on my playlist {playlist}` · `start the playlist {playlist}` · `start my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` |
| Play Radio | `play radio` · `play the radio station {station}` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` |
| Play Random | `Play a random {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` |
| Play Song | `play {song}` · `play {song} by {musician}` · `play the song {song}` · `play song {song}` · `play the song {song} by {musician}` · `play song {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which {query_type} do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` |
| Recommend | `recommend something` · `recommend {media_type}` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` |
| Search Media | `Search for a movie {query}` · `Search for content {query}` · `Find a movie {query}` · `Find content {query}` · `Look for a movie {query}` · `Look for content {query}` |
| Set Reminder | `remind me in {duration_minutes} minutes` · `remind me at {reminder_time}` · `set a reminder for {duration_minutes} minutes` · `set a reminder for {reminder_time}` |
| Show More | `show more` · `next page` · `more results` · `see more` · `more` · `what else` |
| Shuffle Play | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| Skip Forward Back | `skip forward {seek_amount} {seek_unit}` · `skip {seek_direction} {seek_amount} {seek_unit}` · `skip {seek_direction}` · `skip forward` · `skip {seek_amount} seconds` · `skip back {seek_amount} {seek_unit}` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="es-es"></a>Spanish (es-ES)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` |
| Browse Library | `{browse_category}` · `solo {browse_category}` · `quiero {browse_category}` · `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Find Song | `busca una cancion` · `busca una cancion llamada {titleKeywords}` · `ayudame a encontrar una cancion` · `estoy buscando una cancion` · `encuentra una cancion llamada {titleKeywords}` · `encuentra una cancion` |
| Find Song By Artist | `busca una cancion de {musician}` · `ayudame a encontrar una cancion de {musician}` · `estoy buscando una cancion de {musician}` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` · `transferir la música` |
| Go To Chapter | `Siguiente capítulo` · `Ir al capítulo {chapter_number}` · `Capítulo anterior` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Qué está sonando` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` |
| Play Album | `Reproduce el álbum {album}` · `Reproduce el álbum {album} de {musician}` · `un álbum de {musician}` · `Reproduce álbum {album}` · `Escucha el álbum {album}` · `Escucha el álbum {album} de {musician}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` |
| Play Book | `reproduce {book}` · `reproduce el libro {book}` · `escucha {book}` · `escucha el libro {book}` · `escucha el audiolibro {book}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce {genre} de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `reproduce los favoritos de {username}` · `reproduce {media_type} favoritos de {username}` · `Reproduce mi {media_type} favorito` · `pon los favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce contenidos nuevos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Next Episode | `reproduce el próximo episodio de {series_name}` · `reproduce el próximo episodio de la serie {series_name}` · `pon el próximo episodio de {series_name}` · `reproduce el último episodio de {series_name}` · `continúa viendo {series_name}` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` · `Reproduce la playlist {playlist}` · `Reproduce mi playlist {playlist}` · `Pon la playlist {playlist}` · `Pon mi playlist {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Reproduce la estación de radio {station}` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué {query_type} tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` |
| Recommend | `recomienda algo` · `recomienda {media_type}` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` |
| Search Media | `Busca una película {query}` · `Busca un video {query}` · `Busca una serie {query}` · `Busca contenido {query}` · `Encuentra una película {query}` · `Encuentra contenido {query}` |
| Set Reminder | `recuérdame en {duration_minutes} minutos` · `recuérdame a las {reminder_time}` · `pon un recordatorio de {duration_minutes} minutos` · `crea un recordatorio para las {reminder_time}` |
| Show More | `mostrar más` · `más resultados` · `siguiente` · `continúa` · `qué más` · `ver más` |
| Shuffle Play | `reproduce la lista {playlist} en modo aleatorio` · `mezcla la lista {playlist}` · `reproduce la lista de reproducción {playlist} en modo aleatorio` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="es-mx"></a>Spanish - Mexico (es-MX)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` |
| Browse Library | `{browse_category}` · `solo {browse_category}` · `quiero {browse_category}` · `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Find Song | `busca una cancion` · `busca una cancion llamada {titleKeywords}` · `ayudame a encontrar una cancion` · `estoy buscando una cancion` · `encuentra una cancion llamada {titleKeywords}` · `encuentra una cancion` |
| Find Song By Artist | `busca una cancion de {musician}` · `ayudame a encontrar una cancion de {musician}` · `estoy buscando una cancion de {musician}` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` |
| Go To Chapter | `Siguiente capítulo` · `Ir al capítulo {chapter_number}` · `Capítulo anterior` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Qué está sonando` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` |
| Play Album | `Reproduce el álbum {album}` · `Reproduce el álbum {album} de {musician}` · `un álbum de {musician}` · `Reproduce álbum {album}` · `Escucha el álbum {album}` · `Escucha el álbum {album} de {musician}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` |
| Play Book | `reproduce {book}` · `reproduce el libro {book}` · `escucha {book}` · `escucha el libro {book}` · `escucha el audiolibro {book}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce {genre} de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `reproduce los favoritos de {username}` · `reproduce {media_type} favoritos de {username}` · `Reproduce mi {media_type} favorito` · `pon los favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce contenidos nuevos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Next Episode | `reproduce el próximo episodio de {series_name}` · `reproduce el próximo episodio de la serie {series_name}` · `pon el próximo episodio de {series_name}` · `reproduce el último episodio de {series_name}` · `continúa viendo {series_name}` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` · `Reproduce la playlist {playlist}` · `Reproduce mi playlist {playlist}` · `Pon la playlist {playlist}` · `Pon mi playlist {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Reproduce la estación de radio {station}` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué {query_type} tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` |
| Recommend | `recomienda algo` · `recomienda {media_type}` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` |
| Search Media | `Busca una película {query}` · `Busca un video {query}` · `Busca una serie {query}` · `Busca contenido {query}` · `Encuentra una película {query}` · `Encuentra contenido {query}` |
| Set Reminder | `recuérdame en {duration_minutes} minutos` · `recuérdame a las {reminder_time}` · `pon un recordatorio de {duration_minutes} minutos` · `crea un recordatorio para las {reminder_time}` |
| Show More | `mostrar más` · `más resultados` · `siguiente` · `continúa` · `qué más` · `ver más` |
| Shuffle Play | `reproduce la lista {playlist} en modo aleatorio` · `mezcla la lista {playlist}` · `reproduce la lista de reproducción {playlist} en modo aleatorio` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="es-us"></a>Spanish - US (es-US)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` |
| Browse Library | `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` · `qué {browse_category} tengo` · `qué {browse_category} hay` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` |
| Go To Chapter | `Siguiente capítulo` · `Ir al capítulo {chapter_number}` · `Capítulo anterior` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Qué está sonando` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` |
| Play Album | `Reproduce el álbum {album}` · `Reproduce el álbum {album} de {musician}` · `un álbum de {musician}` · `Reproduce álbum {album}` · `Escucha el álbum {album}` · `Escucha el álbum {album} de {musician}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` |
| Play Book | `reproduce {book}` · `reproduce el libro {book}` · `escucha {book}` · `escucha el libro {book}` · `escucha el audiolibro {book}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce {genre} de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `reproduce los favoritos de {username}` · `reproduce {media_type} favoritos de {username}` · `Reproduce mi {media_type} favorito` · `pon los favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce contenidos nuevos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Next Episode | `reproduce el próximo episodio de {series_name}` · `reproduce el próximo episodio de la serie {series_name}` · `pon el próximo episodio de {series_name}` · `reproduce el último episodio de {series_name}` · `continúa viendo {series_name}` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` · `Reproduce la playlist {playlist}` · `Reproduce mi playlist {playlist}` · `Pon la playlist {playlist}` · `Inicia la playlist {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Reproduce la estación de radio {station}` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué {query_type} tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` |
| Recommend | `recomienda algo` · `recomienda {media_type}` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` |
| Search Media | `Busca una película {query}` · `Busca un video {query}` · `Busca una serie {query}` · `Busca contenido {query}` · `Encuentra una película {query}` · `Encuentra contenido {query}` |
| Set Reminder | `recuérdame en {duration_minutes} minutos` · `recuérdame a las {reminder_time}` · `pon un recordatorio de {duration_minutes} minutos` · `crea un recordatorio para las {reminder_time}` |
| Show More | `mostrar más` · `más resultados` · `siguiente` · `continúa` · `qué más` · `ver más` |
| Shuffle Play | `reproduce la lista {playlist} en modo aleatorio` · `mezcla la lista {playlist}` · `reproduce la lista de reproducción {playlist} en modo aleatorio` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="fr-ca"></a>French - Canada (fr-CA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Ajoute {song} à ma file d'attente` · `Ajoute {song} de {musician} à ma file d'attente` · `Ajoute {song} à la file d'attente` · `Ajoute {song} de {musician} à la file d'attente` · `Mets {song} dans la file d'attente` · `Mets {song} de {musician} dans la file d'attente` |
| Browse Library | `{browse_category}` · `juste {browse_category}` · `je veux {browse_category}` · `parcourir {browse_category}` · `montre-moi {browse_category}` · `lister {browse_category}` |
| Clear Queue | `Efface ma file d'attente` · `Efface la file d'attente` · `Vide ma file d'attente` · `Vide la file d'attente` · `Supprime tout de ma file d'attente` · `Efface ma liste` |
| Continue Watching | `Continuer à regarder` · `Continuer à écouter` · `Reprendre où j'en étais` · `Continuer` |
| Find Song | `trouve une chanson` · `trouve une chanson appelee {titleKeywords}` · `aide moi a trouver une chanson` · `je cherche une chanson` · `cherche une chanson appelee {titleKeywords}` · `cherche une chanson` |
| Find Song By Artist | `trouve une chanson de {musician}` · `aide moi a trouver une chanson de {musician}` · `je cherche une chanson de {musician}` |
| Follow Me | `suis-moi` · `continuer la lecture` · `reprendre la lecture` · `transférer la lecture` |
| Go To Chapter | `Chapitre suivant` · `Aller au chapitre {chapter_number}` · `Chapitre précédent` · `Sauter au chapitre {chapter_number}` · `Avancer d'un chapitre` · `Reculer d'un chapitre` |
| In Progress Media List | `qu'est-ce que j'écoute` · `qu'est-ce que je regarde` · `quoi en cours` · `afficher ma progression` · `qu'ai-je commencé` |
| Learn My Voice | `Apprends ma voix` · `Reconnais ma voix` · `Lie ma voix` · `C'est ma voix` · `Reconnais-moi` · `Configure mon profil vocal` |
| List Queue | `Qu'est-ce qu'il y a dans ma file d'attente` · `Qu'est-ce qu'il y a dans la file d'attente` · `Qu'est-ce qui vient après` · `Qu'est-ce qui suit` · `Affiche ma file d'attente` · `Liste ma file d'attente` |
| Loop All Off | `Désactive la boucle` · `Désactive la répétition` |
| Loop All On | `Active la boucle` · `Active la répétition` |
| Loop Song On | `Répète cette chanson` · `Répète ce morceau` · `Mets cette chanson en boucle` · `Loop cette chanson` · `Répète indéfiniment` |
| Mark Favorite | `J'aime bien` · `J'aime cette vidéo` · `J'aime cette chanson` · `J'aime cette musique` · `Ajoute la vidéo aux favoris` · `Ajoute la chanson aux favoris` |
| Media Info | `Quel est le {media_info_type}` · `Qu'est-ce qui joue` · `Quelle est la {media_info_type}` · `Dis-moi le {media_info_type}` · `Dis-moi la {media_info_type}` · `Quel {media_info_type} est-ce` |
| Play Album | `Lis l'album {album}` · `Lis l'album {album} de {musician}` · `un album de {musician}` · `Lis album {album}` · `Écoute l'album {album}` · `Écoute l'album {album} de {musician}` |
| Play Artist Songs | `Lis les chansons de {musician}` · `Lis la musique de {musician}` · `Lis les titres de {musician}` · `Lis les morceaux de {musician}` · `Lis {musician}` · `Écoute {musician}` |
| Play Book | `lis {book}` · `lis le livre {book}` · `écoute {book}` · `écoute le livre {book}` · `écoute le livre audio {book}` |
| Play By Decade | `Lis des chansons des {decade}` · `Lis du {genre} des {decade}` · `Lis des titres des {decade}` · `Lis des succès des {decade}` · `Lis de la musique des {decade}` · `Lis les succès des {decade}` |
| Play By Genre | `Joue de la musique {genre}` · `Joue du {genre}` · `Je veux écouter du {genre}` · `Mets du {genre}` · `Joue des chansons {genre}` |
| Play Channel | `Chaîne {channel}` · `Lis la radio {channel}` · `Radio {channel}` |
| Play Episode | `joue la saison {season_number} épisode {episode_number} de {series_name}` · `joue {series_name} saison {season_number} épisode {episode_number}` · `regarde la saison {season_number} épisode {episode_number} de {series_name}` · `De jouer la saison {season_number} épisode {episode_number} de {series_name}` · `De regarder la saison {season_number} épisode {episode_number} de {series_name}` |
| Play Favorites | `Lis mes {media_type} préférés` · `Lis mes favoris` · `joue les favoris de {username}` · `joue {media_type} favoris de {username}` · `mets les favoris de {username}` · `écoute les favoris de {username}` |
| Play Last Added | `Lis les derniers {media_type} ajoutés` · `Lis les nouveaux médias` · `Lis les nouveautés {media_type}` |
| Play Mood Music | `joue de la musique {mood}` · `joue quelque chose de {mood}` · `je veux de la musique {mood}` · `joue-moi quelque chose de {mood}` |
| Play Next | `Lis {song} ensuite` · `Lis {song} de {musician} ensuite` · `Lis {song} après` · `Lis {song} de {musician} après` · `Je veux entendre {song} ensuite` · `Passe {song} ensuite` |
| Play Next Episode | `joue le prochain épisode de {series_name}` · `joue le prochain épisode de la série {series_name}` · `mets le prochain épisode de {series_name}` · `joue le dernier épisode de {series_name}` · `continue à regarder {series_name}` · `De jouer le prochain épisode de {series_name}` |
| Play Playlist | `Lis la playlist {playlist}` · `Lis ma playlist {playlist}` · `Joue la playlist {playlist}` · `Joue ma playlist {playlist}` · `Mets la playlist {playlist}` · `Lance la playlist {playlist}` |
| Play Podcast | `joue le podcast {podcast_name}` · `écoute le podcast {podcast_name}` · `lance le podcast {podcast_name}` · `joue podcast {podcast_name}` · `écoute le dernier épisode de {podcast_name}` · `je veux écouter le podcast {podcast_name}` |
| Play Radio | `Lis la radio` · `Lis la station de radio {station}` · `Démarre la radio` · `Lis le mode radio` · `Lis de la musique similaire` · `Lis des chansons similaires` |
| Play Random | `Joue un {media_type} aléatoire` · `Joue quelque chose au hasard` · `Joue un {media_type} aléatoire de {genre}` · `Joue des {media_type} au hasard` · `Lance un {media_type} aléatoire` · `Surprends-moi avec des {media_type}` |
| Play Song | `Lis {song}` · `Lis {song} de {musician}` · `Lis la chanson {song}` · `Lis chanson {song}` · `Lis la chanson {song} de {musician}` · `Écoute {song}` |
| Play Video | `Lis la vidéo {title}` · `Je veux regarder {title}` · `On regarde {title}` · `Je voudrais voir {title}` · `montre-moi {title}` · `peux-tu me montrer {title}` |
| Query Artist Library | `Quelles chansons avons-nous de {musician}` · `Quels {query_type} avons-nous de {musician}` · `Quels titres avons-nous de {musician}` · `Quels albums avons-nous de {musician}` · `Quels disques avons-nous de {musician}` · `Qu'avons-nous de {musician}` |
| Query Recently Added | `quoi de neuf` · `qu'est-ce qui a été ajouté récemment` · `montre-moi les nouveautés` · `y a-t-il du nouveau` · `quels sont les derniers ajouts` · `quoi de neuf dans ma bibliothèque` |
| Recommend | `recommande quelque chose` · `recommande {media_type}` · `recommande de la musique` · `recommande un film` · `suggère quelque chose` · `joue quelque chose que j'aimerais` |
| Repeat Single On | `Répète la chanson` · `Répète le morceau` · `Répète la vidéo` · `Répète` |
| Search Media | `Cherche un film {query}` · `Cherche un contenu {query}` · `Cherche une série {query}` · `Cherche un vidéo {query}` · `Trouve un film {query}` · `Trouve un contenu {query}` |
| Set Reminder | `rappelle-moi dans {duration_minutes} minutes` · `rappelle-moi à {reminder_time}` · `mets un rappel de {duration_minutes} minutes` · `crée un rappel pour {reminder_time}` |
| Show More | `montre plus` · `encore` · `plus de résultats` · `continuer` · `suivant` · `quoi d'autre` |
| Shuffle Play | `lis la playlist {playlist} en mode aléatoire` · `mélange la playlist {playlist}` · `mets la playlist {playlist} en mode aléatoire` |
| Sleep Timer | `arrêter dans {duration_minutes} minutes` · `minuterie {duration_minutes} minutes` · `arrêter après {duration_minutes} minutes` · `éteindre dans {duration_minutes} minutes` |
| Turn Radio Off | `Désactive le mode radio` · `Éteins le mode radio` · `Mode radio désactivé` · `Désactive la radio` · `Éteins la radio` · `Arrête le mode radio` |
| Turn Radio On | `Active le mode radio` · `Allume le mode radio` · `Mode radio activé` · `Active la radio` · `Allume la radio` |
| Unmark Favorite | `Je n'aime pas ça` · `Je n'aime pas cette vidéo` · `Je n'aime pas cette chanson` · `Je n'aime pas cette musique` · `Retire la vidéo des favoris` · `Retire la chanson des favoris` |
| Who Am I | `Qui suis-je` · `Quel compte est-ce` · `Quel compte j'utilise` · `Qui parle` · `Quel profil est actif` · `Suis-je reconnu` |

### <a id="fr-fr"></a>French (fr-FR)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Ajoute {song} à ma file d'attente` · `Ajoute {song} de {musician} à ma file d'attente` · `Ajoute {song} à la file d'attente` · `Ajoute {song} de {musician} à la file d'attente` · `Mets {song} dans la file d'attente` · `Mets {song} de {musician} dans la file d'attente` |
| Browse Library | `{browse_category}` · `juste {browse_category}` · `je veux {browse_category}` · `parcourir {browse_category}` · `montre-moi {browse_category}` · `lister {browse_category}` |
| Clear Queue | `Efface ma file d'attente` · `Efface la file d'attente` · `Vide ma file d'attente` · `Vide la file d'attente` · `Supprime tout de ma file d'attente` · `Efface ma liste` |
| Continue Watching | `Continuer à regarder` · `Continuer à écouter` · `Reprendre où j'en étais` · `Continuer` |
| Find Song | `trouve une chanson` · `trouve une chanson appelee {titleKeywords}` · `aide moi a trouver une chanson` · `je cherche une chanson` · `cherche une chanson appelee {titleKeywords}` · `cherche une chanson` |
| Find Song By Artist | `trouve une chanson de {musician}` · `aide moi a trouver une chanson de {musician}` · `je cherche une chanson de {musician}` |
| Follow Me | `suis-moi` · `continuer la lecture` · `reprendre la lecture` · `transférer la lecture` · `reprendre où j'en étais` |
| Go To Chapter | `Chapitre suivant` · `Aller au chapitre {chapter_number}` · `Chapitre précédent` · `Sauter au chapitre {chapter_number}` · `Avancer d'un chapitre` · `Reculer d'un chapitre` |
| In Progress Media List | `qu'est-ce que j'écoute` · `qu'est-ce que je regarde` · `quoi en cours` · `afficher ma progression` · `qu'ai-je commencé` |
| Learn My Voice | `Apprends ma voix` · `Reconnais ma voix` · `Lie ma voix` · `C'est ma voix` · `Reconnais-moi` · `Configure mon profil vocal` |
| List Queue | `Qu'est-ce qu'il y a dans ma file d'attente` · `Qu'est-ce qu'il y a dans la file d'attente` · `Qu'est-ce qui vient après` · `Qu'est-ce qui suit` · `Affiche ma file d'attente` · `Liste ma file d'attente` |
| Loop All Off | `Désactive la boucle` · `Désactive la répétition` |
| Loop All On | `Active la boucle` · `Active la répétition` |
| Loop Song On | `Répète cette chanson` · `Répète ce morceau` · `Mets cette chanson en boucle` · `Loop cette chanson` · `Répète indéfiniment` |
| Mark Favorite | `J'aime bien` · `J'aime cette vidéo` · `J'aime cette chanson` · `J'aime cette musique` · `Ajoute la vidéo aux favoris` · `Ajoute la chanson aux favoris` |
| Media Info | `Quel est le {media_info_type}` · `Qu'est-ce qui joue` · `Quelle est la {media_info_type}` · `Dis-moi le {media_info_type}` · `Dis-moi la {media_info_type}` · `Quel {media_info_type} est-ce` |
| Play Album | `Lis l'album {album}` · `Lis l'album {album} de {musician}` · `un album de {musician}` · `Lis album {album}` · `Écoute l'album {album}` · `Écoute l'album {album} de {musician}` |
| Play Artist Songs | `Lis les chansons de {musician}` · `Lis la musique de {musician}` · `Lis les titres de {musician}` · `Lis les morceaux de {musician}` · `Lis {musician}` · `Écoute {musician}` |
| Play Book | `lis {book}` · `lis le livre {book}` · `écoute {book}` · `écoute le livre {book}` · `écoute le livre audio {book}` |
| Play By Decade | `Lis des chansons des {decade}` · `Lis du {genre} des {decade}` · `Lis des titres des {decade}` · `Lis des succès des {decade}` · `Lis de la musique des {decade}` · `Lis les succès des {decade}` |
| Play By Genre | `Joue de la musique {genre}` · `Joue du {genre}` · `Je veux écouter du {genre}` · `Mets du {genre}` · `Joue des chansons {genre}` |
| Play Channel | `Chaîne {channel}` · `Lis la radio {channel}` · `Radio {channel}` |
| Play Episode | `joue la saison {season_number} épisode {episode_number} de {series_name}` · `joue {series_name} saison {season_number} épisode {episode_number}` · `regarde la saison {season_number} épisode {episode_number} de {series_name}` · `De jouer la saison {season_number} épisode {episode_number} de {series_name}` · `De regarder la saison {season_number} épisode {episode_number} de {series_name}` |
| Play Favorites | `Lis mes {media_type} préférés` · `Lis mes favoris` · `joue les favoris de {username}` · `joue {media_type} favoris de {username}` · `mets les favoris de {username}` · `écoute les favoris de {username}` |
| Play Last Added | `Lis les derniers {media_type} ajoutés` · `Lis les nouveaux médias` · `Lis les nouveautés {media_type}` |
| Play Mood Music | `joue de la musique {mood}` · `joue quelque chose de {mood}` · `je veux de la musique {mood}` · `joue-moi quelque chose de {mood}` |
| Play Next | `Lis {song} ensuite` · `Lis {song} de {musician} ensuite` · `Lis {song} après` · `Lis {song} de {musician} après` · `Je veux entendre {song} ensuite` · `Passe {song} ensuite` |
| Play Next Episode | `joue le prochain épisode de {series_name}` · `joue le prochain épisode de la série {series_name}` · `mets le prochain épisode de {series_name}` · `joue le dernier épisode de {series_name}` · `continue à regarder {series_name}` · `De jouer le prochain épisode de {series_name}` |
| Play Playlist | `Lis la playlist {playlist}` · `Lis ma playlist {playlist}` · `Joue la playlist {playlist}` · `Joue ma playlist {playlist}` · `Mets la playlist {playlist}` · `Lance la playlist {playlist}` |
| Play Podcast | `joue le podcast {podcast_name}` · `écoute le podcast {podcast_name}` · `lance le podcast {podcast_name}` · `joue podcast {podcast_name}` · `écoute le dernier épisode de {podcast_name}` · `je veux écouter le podcast {podcast_name}` |
| Play Radio | `Lis la radio` · `Lis la station de radio {station}` · `Démarre la radio` · `Lis le mode radio` · `Lis de la musique similaire` · `Lis des chansons similaires` |
| Play Random | `Joue un {media_type} aléatoire` · `Joue quelque chose au hasard` · `Joue un {media_type} aléatoire de {genre}` · `Joue des {media_type} au hasard` · `Lance un {media_type} aléatoire` · `Surprends-moi avec des {media_type}` |
| Play Song | `Lis {song}` · `Lis {song} de {musician}` · `Lis la chanson {song}` · `Lis chanson {song}` · `Lis la chanson {song} de {musician}` · `Écoute {song}` |
| Play Video | `Lis la vidéo {title}` · `Je veux regarder {title}` · `On regarde {title}` · `Je voudrais voir {title}` · `montre-moi {title}` · `peux-tu me montrer {title}` |
| Query Artist Library | `Quelles chansons avons-nous de {musician}` · `Quels {query_type} avons-nous de {musician}` · `Quels titres avons-nous de {musician}` · `Quels albums avons-nous de {musician}` · `Quels disques avons-nous de {musician}` · `Qu'avons-nous de {musician}` |
| Query Recently Added | `quoi de neuf` · `qu'est-ce qui a été ajouté récemment` · `montre-moi les nouveautés` · `y a-t-il du nouveau` · `quels sont les derniers ajouts` · `quoi de neuf dans ma bibliothèque` |
| Recommend | `recommande quelque chose` · `recommande {media_type}` · `recommande de la musique` · `recommande un film` · `suggère quelque chose` · `joue quelque chose que j'aimerais` |
| Repeat Single On | `Répète la chanson` · `Répète le morceau` · `Répète la vidéo` · `Répète` |
| Search Media | `Cherche un film {query}` · `Cherche un contenu {query}` · `Cherche une série {query}` · `Cherche un vidéo {query}` · `Trouve un film {query}` · `Trouve un contenu {query}` |
| Set Reminder | `rappelle-moi dans {duration_minutes} minutes` · `rappelle-moi à {reminder_time}` · `mets un rappel de {duration_minutes} minutes` · `crée un rappel pour {reminder_time}` |
| Show More | `montre plus` · `encore` · `plus de résultats` · `continuer` · `suivant` · `quoi d'autre` |
| Shuffle Play | `lis la playlist {playlist} en mode aléatoire` · `mélange la playlist {playlist}` · `mets la playlist {playlist} en mode aléatoire` |
| Sleep Timer | `arrêter dans {duration_minutes} minutes` · `minuterie {duration_minutes} minutes` · `arrêter après {duration_minutes} minutes` · `éteindre dans {duration_minutes} minutes` |
| Turn Radio Off | `Désactive le mode radio` · `Éteins le mode radio` · `Mode radio désactivé` · `Désactive la radio` · `Éteins la radio` · `Arrête le mode radio` |
| Turn Radio On | `Active le mode radio` · `Allume le mode radio` · `Mode radio activé` · `Active la radio` · `Allume la radio` |
| Unmark Favorite | `Je n'aime pas ça` · `Je n'aime pas cette vidéo` · `Je n'aime pas cette chanson` · `Je n'aime pas cette musique` · `Retire la vidéo des favoris` · `Retire la chanson des favoris` |
| Who Am I | `Qui suis-je` · `Quel compte est-ce` · `Quel compte j'utilise` · `Qui parle` · `Quel profil est actif` · `Suis-je reconnu` |

### <a id="hi-in"></a>Hindi (hi-IN)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `{song} कतार में जोड़ो` · `{musician} का {song} कतार में जोड़ो` · `{song} कतार में डालो` · `{musician} का {song} कतार में डालो` |
| Browse Library | `{browse_category}` · `{browse_category} ब्राउज़ करो` · `मुझे {browse_category} दिखाओ` · `{browse_category} की लिस्ट दो` · `मेरे पास कौन से {browse_category} हैं` · `क्या {browse_category} हैं` |
| Clear Queue | `कतार साफ़ करो` · `कतार खाली करो` · `कतार से सब हटाओ` |
| Continue Watching | `देखना जारी रखो` · `सुनना जारी रखो` · `जहाँ छोड़ा था वहाँ से फिर से शुरू करो` · `मैं क्या देख रहा था` · `जारी रखो` · `जारी` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `मेरे साथ आओ` · `चलाना जारी रखो` · `जहां छोड़ा थे वहां से शुरू करो` |
| Go To Chapter | `अगला चैप्टर` · `चैप्टर {chapter_number} पर जाओ` · `पिछला चैप्टर` · `चैप्टर {chapter_number} पर स्किप करो` · `एक चैप्टर आगे जाओ` · `एक चैप्टर पीछे जाओ` |
| In Progress Media List | `मैं क्या सुन रहा हूँ` · `मैं क्या देख रहा हूँ` · `क्या प्रगति पर है` · `मेरी प्रगति दिखाओ` · `मैं क्या चला रहा था` · `मेरी प्रगति पर मीडिया दिखाओ` |
| Learn My Voice | `मेरी आवाज़ सीखो` · `मेरी आवाज़ याद रखो` · `मुझे पहचानो` · `मेरी आवाज़ लिंक करो` · `यह मेरी आवाज़ है` · `मेरा वॉइस प्रोफाइल सेटअप करो` |
| List Queue | `कतार में क्या है` · `क्या आने वाला है` · `क्या अगला है` · `कतार दिखाओ` · `अगला क्या चलेगा` |
| Loop Song On | `इस गाने को लूप करो` · `इस गाने को हमेशा लूप करो` · `गाना दोहराओ` · `इस गाने को दोहराओ` · `इस गाने को हमेशा दोहराओ` |
| Mark Favorite | `मुझे यह पसंद है` · `मुझे वीडियो पसंद है` · `मुझे गाना पसंद है` · `मुझे म्यूज़िक पसंद है` · `मुझे यह गाना पसंद है` · `वीडियो को पसंदीदा में जोड़ो` |
| Media Info | `गाने का नाम क्या है` · `यह {media_info_type} क्या है` · `वीडियो का नाम क्या है` · `म्यूज़िक का नाम क्या है` · `गाने का शीर्षक क्या है` · `अभी क्या चल रहा है` |
| Play Album | `एल्बम {album} चलाओ` · `एल्बम {album} {musician} का चलाओ` · `{musician} का एल्बम चलाओ` · `एल्बम {album} सुनो` · `{musician} का एल्बम सुनो` |
| Play Artist Songs | `{musician} के गाने चलाओ` · `{musician} की म्यूज़िक चलाओ` · `{musician} के ट्रैक चलाओ` · `{musician} सुनो` · `{musician} के गाने सुनो` · `{musician} की म्यूज़िक सुनो` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `{decade} के गाने चलाओ` · `{genre} {decade} से चलाओ` · `{decade} के ट्रैक चलाओ` · `{decade} की म्यूज़िक चलाओ` · `{decade} हिट्स चलाओ` · `मैं {decade} म्यूज़िक सुनना चाहता हूँ` |
| Play By Genre | `{genre} म्यूज़िक चलाओ` · `{genre} गाने चलाओ` · `मुझे {genre} म्यूज़िक चलाओ` · `मैं {genre} सुनना चाहता हूँ` · `{genre} चलाओ` · `मुझे {genre} म्यूज़िक दो` |
| Play Channel | `चैनल {channel} चलाओ` · `रेडियो {channel} चलाओ` |
| Play Episode | `{series_name} सीज़न {season_number} एपिसोड {episode_number} चलाओ` · `सीज़न {season_number} एपिसोड {episode_number} {series_name} का चलाओ` · `{series_name} सीज़न {season_number} एपिसोड {episode_number} देखो` |
| Play Favorites | `मेरी पसंदीदा {media_type} चलाओ` · `मेरी पसंदीदा चलाओ` · `{username} की पसंदीदा चलाओ` · `{username} की पसंदीदा {media_type} चलाओ` · `मेरे पसंदीदा गाने चलाओ` · `मेरी पसंदीदा म्यूज़िक चलाओ` |
| Play Last Added | `आखिरी जोड़े {media_type} चलाओ` · `नया मीडिया चलाओ` · `{media_type} जोड़े {time_period} चलाओ` · `हाल ही में जोड़े {media_type} चलाओ` · `कुछ नया चलाओ` · `हाल ही में जोड़े {media_type} {time_period} चलाओ` |
| Play Mood Music | `{mood} म्यूज़िक चलाओ` · `कुछ {mood} चलाओ` · `{mood} गाने चलाओ` · `मुझे {mood} म्यूज़िक चाहिए` |
| Play Next | `{song} अगला चलाओ` · `{musician} का {song} अगला चलाओ` · `मैं {song} अगला सुनना चाहता हूँ` · `{song} इसके बाद चलाओ` |
| Play Next Episode | `{series_name} का अगला एपिसोड चलाओ` · `{series_name} का नवीनतम एपिसोड चलाओ` · `{series_name} देखना जारी रखो` |
| Play Playlist | `प्लेलिस्ट {playlist} चलाओ` · `मेरी प्लेलिस्ट {playlist} चलाओ` · `प्लेलिस्ट {playlist} लगाओ` · `प्लेलिस्ट {playlist} शुरू करो` · `प्लेलिस्ट {playlist} सुनो` · `क्या तुम प्लेलिस्ट {playlist} चला सकते हो` |
| Play Podcast | `पॉडकास्ट {podcast_name} चलाओ` · `पॉडकास्ट {podcast_name} सुनो` · `{podcast_name} का ताज़ा एपिसोड चलाओ` · `पॉडकास्ट {podcast_name} शुरू करो` · `मैं {podcast_name} पॉडकास्ट सुनना चाहता हूँ` |
| Play Radio | `रेडियो चलाओ` · `रेडियो स्टेशन {station} चलाओ` · `रेडियो मोड चलाओ` · `रेडियो शुरू करो` · `ऐसा ही और चलाओ` · `समान म्यूज़िक चलाते रहो` |
| Play Random | `रैंडम {media_type} चलाओ` · `कुछ रैंडम चलाओ` · `रैंडम {media_type} {genre} से चलाओ` · `अपने {media_type} शफल करो` · `रैंडम गाने चलाओ` · `रैंडम म्यूज़िक चलाओ` |
| Play Song | `{song} चलाओ` · `{musician} का {song} चलाओ` · `गाना {song} {musician} का चलाओ` · `गाना {song} चलाओ` · `{song} गाना चलाओ` · `{song} सुनो` |
| Play Video | `वीडियो {title} चलाओ` · `वीडियो {title} लगाओ` · `{title} शुरू करो` · `{title} देखो` · `क्या तुम {title} चला सकते हो` · `मैं {title} देखना चाहता हूँ` |
| Query Artist Library | `{musician} के कौन से ट्रैक हैं` · `{musician} के {query_type} दिखाओ` · `{musician} के कौन से गाने हैं` · `{musician} के कौन से एल्बम हैं` · `{musician} के पास क्या है` · `{musician} के ट्रैक दिखाओ` |
| Query Recently Added | `क्या नया है` · `हाल ही में क्या जोड़ा गया` · `मेरी लाइब्रेरी में क्या नया है` · `हाल ही में जोड़े गए दिखाओ` · `कुछ नया है क्या` · `नए जोड़े गए आइटम दिखाओ` |
| Recommend | `कुछ सुझाव दो` · `{media_type} सुझाओ` · `कुछ म्यूज़िक सुझाओ` · `एक फिल्म सुझाओ` · `देखने के लिए कुछ सुझाओ` |
| Search Media | `एक फिल्म {query} खोजो` · `कंटेंट {query} खोजो` · `एक वीडियो {query} खोजो` · `एक फिल्म {query} ढूंढो` · `कंटेंट {query} ढूंढो` · `एक वीडियो {query} ढूंढो` |
| Set Reminder | `{duration_minutes} मिनट में मुझे याद दिलाओ` · `{reminder_time} पर मुझे याद दिलाओ` · `{duration_minutes} मिनट का रिमाइंडर सेट करो` · `{reminder_time} का रिमाइंडर सेट करो` |
| Show More | `और दिखाओ` · `अगला पेज` · `और` · `जारी रखो` · `आगे` · `क्या और है` |
| Shuffle Play | `प्लेलिस्ट {playlist} शफल में चलाओ` · `प्लेलिस्ट {playlist} शफल करो` |
| Sleep Timer | `{duration_minutes} मिनट में बंद करो` · `स्लीप टाइमर {duration_minutes} मिनट सेट करो` · `स्लीप टाइमर {duration_minutes} मिनट` · `{duration_minutes} मिनट बाद बंद करो` |
| Turn Radio Off | `रेडियो मोड बंद करो` · `रेडियो मोड डिसेबल करो` · `रेडियो मोड ऑफ` · `रेडियो बंद करो` · `रेडियो डिसेबल करो` |
| Turn Radio On | `रेडियो मोड चालू करो` · `रेडियो मोड एनेबल करो` · `रेडियो मोड ऑन` · `रेडियो चालू करो` · `रेडियो एनेबल करो` |
| Unmark Favorite | `मुझे यह पसंद नहीं है` · `मुझे वीडियो पसंद नहीं है` · `मुझे गाना पसंद नहीं है` · `मुझे म्यूज़िक पसंद नहीं है` · `वीडियो को पसंदीदा से हटाओ` · `गाने को पसंदीदा से हटाओ` |
| Who Am I | `मैं कौन हूँ` · `यह कौन सा खाता है` · `मैं कौन सा खाता इस्तेमाल कर रहा हूँ` · `कौन बोल रहा है` · `कौन सा प्रोफाइल एक्टिव है` |

### <a id="it-it"></a>Italian (it-IT)

Invocation name: **"mia collezione"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `aggiungi {song} alla coda` · `accoda {song} di {musician}` · `metti {song} in coda` · `accoda {song}` · `aggiungi {song} di {musician} alla coda` · `metti {song} di {musician} in coda` |
| Browse Library | `Sfoglia {browse_category}` · `Sfoglia i {browse_category}` · `Mostra {browse_category}` · `Mostra i {browse_category}` · `Elenca {browse_category}` · `Elenca i {browse_category}` |
| Clear Queue | `svuota la coda` · `pulisci la coda` · `cancella la coda` · `elimina la coda` · `rimuovi tutto dalla coda` · `svuota la mia coda` |
| Continue Watching | `Continua a guardare` · `Riprendi a guardare` · `Continua il video` · `Riprendi il video` |
| Find Song | `cerca una canzone` · `trova una canzone chiamata {titleKeywords}` · `aiutami a trovare una canzone` · `sto cercando una canzone` · `voglio trovare una canzone` · `trova una canzone` |
| Find Song By Artist | `cerca una canzone di {musician}` · `cerca una canzone dei {musician}` · `cerca una canzone degli {musician}` · `cerca una canzone delle {musician}` · `aiutami a trovare una canzone di {musician}` · `aiutami a trovare una canzone dei {musician}` |
| Follow Me | `seguimi` · `seguirmi` · `seguir mi` · `continua ad ascoltare` · `riprendi da dove ero rimasto` · `riprendi da dove ero rimasta` |
| Go To Chapter | `Vai al capitolo {chapter_number}` · `Vai al capitolo {direction}` · `Salta al capitolo {chapter_number}` · `Salta al capitolo {direction}` · `Capitolo {chapter_number}` · `Capitolo {direction}` |
| In Progress Media List | `Cosa stavo guardando` · `Mostrami i video in corso` · `Riprendi a guardare` · `Cosa ho lasciato a metà` · `Cosa ho lasciato a meta` · `quali video non ho finito` |
| Learn My Voice | `Impara la mia voce` · `Riconosci la mia voce` · `Associa la mia voce` · `Collega la mia voce` · `Impara chi sono` · `Riconoscimi` |
| List Queue | `cosa c'è nella coda` · `cosa c'è in coda` · `cosa c'è dopo` · `cosa suona dopo` · `mostra la coda` · `elenca la coda` |
| Loop All Off | `Disattiva loop` · `Disattiva ripetizione` |
| Loop All On | `Attiva loop` · `Attiva ripetizione` |
| Loop Song On | `ripeti questo brano` · `ripeti questa canzone` · `metti in loop questo brano` · `metti in loop questa canzone` · `ripeti per sempre questo brano` |
| Mark Favorite | `Aggiungi ai preferiti` · `Metti nei preferiti` · `Segna come preferito` · `Mi piace` · `Aggiungi questo ai preferiti` · `Metti questo nei preferiti` |
| Media Info | `Cosa sta suonando` · `{media_info_type}` · `Cosa sta suonando adesso` · `Che brano è questo` · `Che canzone è questa` · `Chi è questo artista` |
| Play Album | `Riproduci l'album {album}` · `Riproduci l'album {album} di {musician}` · `un disco di {musician}` · `Riproduci il disco {album}` · `Riproduci album {album}` · `Riproduci disco {album}` |
| Play Artist Songs | `brani di {musician}` · `brani dei {musician}` · `brani degli {musician}` · `brani delle {musician}` · `canzoni di {musician}` · `canzoni dei {musician}` |
| Play Book | `riproduci il libro {book}` · `riproduci l'audiolibro {book}` · `ascolta il libro {book}` · `ascolta l'audiolibro {book}` · `suona il libro {book}` · `metti il libro {book}` |
| Play By Decade | `Riproduci musica degli anni {decade}` · `Suona musica degli anni {decade}` · `Metti musica degli anni {decade}` · `Musica anni {decade}` · `Brani degli anni {decade}` · `Canzoni degli anni {decade}` |
| Play By Genre | `Riproduci {genre}` · `Suona {genre}` · `Metti {genre}` · `Pleia {genre}` · `Di riprodurre {genre}` · `Riproduci genere {genre}` |
| Play Channel | `Canale {channel}` · `Riproduci radio {channel}` · `di riprodurre la radio {channel}` · `di mettere la radio {channel}` · `Radio {channel}` |
| Play Episode | `Riproduci {series_name} stagione {season_number} episodio {episode_number}` · `Riproduci la stagione {season_number} episodio {episode_number} di {series_name}` · `Suona {series_name} stagione {season_number} episodio {episode_number}` · `Metti {series_name} stagione {season_number} episodio {episode_number}` · `Suona la stagione {season_number} episodio {episode_number} di {series_name}` · `Metti la stagione {season_number} episodio {episode_number} di {series_name}` |
| Play Favorites | `Riproduci i miei preferiti` · `Riproduci {media_type} preferiti` · `Suona i miei preferiti` · `Suona {media_type} preferiti` · `Metti i miei preferiti` · `Metti {media_type} preferiti` |
| Play Last Added | `Riproduci novità {media_type}` · `Riproduci nuovi media` · `Riproduci {media_type} aggiunti {time_period}` · `Riproduci ultimi {media_type} aggiunti` · `Suona novità {media_type}` · `Suona ultimi {media_type} aggiunti` |
| Play Mood Music | `Musica {mood}` · `Musica per {mood}` · `Riproduci musica {mood}` · `Riproduci musica per {mood}` · `Suona musica {mood}` · `Suona musica per {mood}` |
| Play Next | `riproduci {song} dopo` · `riproduci {song} di {musician} dopo` · `suona {song} dopo` · `metti {song} dopo` · `suona {song} di {musician} dopo` · `voglio ascoltare {song} dopo` |
| Play Next Episode | `Riproduci il prossimo episodio di {series_name}` · `Metti il prossimo episodio di {series_name}` · `Guarda il prossimo episodio di {series_name}` · `Riproduci l'ultimo episodio di {series_name}` · `Metti l'ultimo episodio di {series_name}` · `Continua a guardare {series_name}` |
| Play Playlist | `Riproduci playlist {playlist}` · `Suona playlist {playlist}` · `Metti playlist {playlist}` · `Pleia playlist {playlist}` · `Ascolta playlist {playlist}` · `Riproduci la playlist {playlist}` |
| Play Podcast | `Riproduci il podcast {podcast_name}` · `Suona il podcast {podcast_name}` · `Ascolta il podcast {podcast_name}` · `Metti il podcast {podcast_name}` · `Ascolta l'ultimo episodio di {podcast_name}` |
| Play Radio | `riproduci radio` · `riproduci la stazione radio {station}` · `suona radio` · `metti radio` · `attiva la radio` · `modalità radio` |
| Play Random | `Riproduci {media_type} casuali` · `Riproduci {media_type} a caso` · `Suona {media_type} casuali` · `Suona {media_type} a caso` · `Metti {media_type} casuali` · `Metti {media_type} a caso` |
| Play Song | `Riproduci il brano {song}` · `Riproduci {song} di {musician}` · `Riproduci la canzone {song}` · `Riproduci il pezzo {song}` · `Riproduci la traccia {song}` · `Suona il brano {song}` |
| Play Video | `Riproduci {title}` · `Suona {title}` · `Metti {title}` · `Pleia {title}` · `voglio guardare {title}` · `fai vedere {title}` |
| Query Artist Library | `Quali brani abbiamo di {musician}` · `Quali {query_type} abbiamo di {musician}` · `Quali canzoni abbiamo di {musician}` · `Che brani abbiamo di {musician}` · `Che canzoni abbiamo di {musician}` · `Quali album abbiamo di {musician}` |
| Query Recently Added | `cosa c'è di nuovo` · `cosa è stato aggiunto di recente` · `quali novità ci sono` · `mostra le novità` · `mostrami gli ultimi aggiunti` · `ci sono novità` |
| Recommend | `Consiglia {media_type}` · `Suggerisci una canzone` · `Raccomanda {media_type}` · `Suggerisci {media_type}` · `Di consigliare {media_type}` · `Di raccomandare {media_type}` |
| Repeat Single On | `Ripeti la canzone` · `Ripeti la traccia` · `Ripeti il brano` · `Ripeti il video` · `di ripeter la canzone` · `di ripeter la traccia` |
| Search Media | `Cerca il contenuto {query}` · `Cerca un film {query}` · `Cerca un video {query}` · `Cerca una serie {query}` · `Cerca un audiolibro {query}` · `Trova il contenuto {query}` |
| Set Reminder | `Ricordami tra {duration_minutes} minuti` · `Ricordami alle {reminder_time}` · `Imposta un promemoria tra {duration_minutes} minuti` · `Imposta un promemoria per le {reminder_time}` |
| Show More | `mostra di più` · `altra pagina` · `più risultati` · `vedi altro` · `altro` · `cos'altro` |
| Shuffle Play | `Mescola la playlist {playlist}` · `Mescola playlist {playlist}` · `Riproduci la playlist {playlist} in modalità casuale` · `Riproduci la playlist {playlist} a caso` · `Suona la playlist {playlist} in modalità casuale` |
| Sleep Timer | `Imposta timer {duration_minutes}` · `Timer per dormire {duration_minutes}` · `Spegimento automatico {duration_minutes}` · `Ferma dopo {duration_minutes}` |
| Turn Radio Off | `disattiva la radio` · `spegni la radio` · `disattiva modalità radio` · `modalità radio spenta` · `ferma riproduzione radio` · `radio spenta` |
| Turn Radio On | `attiva la radio` · `accendi la radio` · `attiva modalità radio` · `modalità radio accesa` · `radio accesa` · `abilita radio` |
| Unmark Favorite | `Rimuovi dai preferiti` · `Togli dai preferiti` · `Rimuovi questo dai preferiti` · `Togli questo dai preferiti` · `Non mi piace più` · `Non mi piace piu` |
| Who Am I | `Chi sono` · `Chi sto usando` · `Con che utente sono collegato` · `Dimmi chi sono` · `Che utente sono` |

### <a id="ja-jp"></a>Japanese (ja-JP)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `{song} をキューに追加して` · `{musician} の {song} をキューに追加して` · `{song} をキューに入れて` · `{musician} の {song} をキューに入れて` |
| Browse Library | `{browse_category}` · `{browse_category} をブラウズして` · `{browse_category} を見せて` · `{browse_category} のリスト` · `どんな {browse_category} がある` · `{browse_category} は何がある` |
| Clear Queue | `キューをクリアして` · `キューを空にして` · `キューから全部消して` |
| Continue Watching | `続きを見て` · `続きを聴いて` · `途中から再開して` · `何を見てたっけ` · `続き` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `ついてきて` · `再生を続けて` · `続きから再生` · `再生を引き継ぐ` |
| Go To Chapter | `次のチャプター` · `チャプター {chapter_number} へ行って` · `前のチャプター` · `チャプター {chapter_number} へスキップして` · `チャプターをスキップして` |
| In Progress Media List | `何聴いてたっけ` · `何見てたっけ` · `進行中のものは` · `進捗を見せて` · `何再生してたっけ` · `開始したものは何` |
| Learn My Voice | `私の声を覚えて` · `私の声を記憶して` · `私を認識して` · `私の声をリンクして` · `これは私の声` · `ボイスプロフィールを設定して` |
| List Queue | `キューには何がある` · `次は何` · `キューを見せて` · `次に何が来る` · `キューのリスト` |
| Loop Song On | `この曲をループして` · `この曲をずっとループして` · `曲をリピートして` · `この曲をリピートして` |
| Mark Favorite | `いいね` · `ビデオいいね` · `曲いいね` · `音楽いいね` · `この曲いいね` · `これいいね` |
| Media Info | `曲の名前は何` · `{media_info_type} は何` · `ビデオの名前は何` · `音楽の名前は何` · `曲のタイトルは何` · `今何が再生中` |
| Play Album | `アルバム {album} を再生して` · `アルバム {album} {musician} を再生して` · `{musician} のアルバムを再生して` · `{musician} のアルバムを聴かせて` |
| Play Artist Songs | `{musician} の曲を再生して` · `{musician} の音楽を再生して` · `{musician} のトラックを再生して` · `{musician} を聴かせて` · `{musician} の曲を聴かせて` · `{musician} を聞きたい` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `{decade} の曲を再生して` · `{genre} の {decade} を再生して` · `{decade} の音楽を再生して` · `{decade} のヒットを再生して` · `{decade} の音楽を聴きたい` · `{decade} の曲を聴かせて` |
| Play By Genre | `{genre} の音楽を再生して` · `{genre} の曲を再生して` · `{genre} を再生して` · `{genre} を聴きたい` · `{genre} の音楽を流して` · `{genre} のストリーム` |
| Play Channel | `チャンネル {channel} を再生して` · `ラジオ {channel} を再生して` |
| Play Episode | `{series_name} のシーズン {season_number} エピソード {episode_number} を再生して` · `シーズン {season_number} エピソード {episode_number} の {series_name} を再生して` · `{series_name} のシーズン {season_number} エピソード {episode_number} を見たい` |
| Play Favorites | `お気に入りの {media_type} を再生して` · `お気に入りを再生して` · `{username} のお気に入りを再生して` · `{username} のお気に入りの {media_type} を再生して` · `お気に入りの曲を再生して` · `お気に入りの音楽を再生して` |
| Play Last Added | `最新の {media_type} を再生して` · `新しいメディアを再生して` · `{time_period} 追加された {media_type} を再生して` · `最近追加された {media_type} を再生して` · `何か新しいものを再生して` · `新着 {media_type} を再生して` |
| Play Mood Music | `{mood} の音楽を再生して` · `{mood} な音楽を再生して` · `{mood} な曲を再生して` · `{mood} の音楽が聴きたい` |
| Play Next | `次に {song} を再生して` · `次に {musician} の {song} を再生して` · `{song} を次に聴きたい` · `{song} をこの後に再生して` |
| Play Next Episode | `{series_name} の次のエピソードを再生して` · `{series_name} の最新のエピソードを再生して` · `{series_name} を続きから観て` |
| Play Playlist | `プレイリスト {playlist} を再生して` · `プレイリスト {playlist} を流して` · `プレイリスト {playlist} をスタートして` · `プレイリスト {playlist} を聴かせて` · `プレイリスト {playlist} を聞きたい` · `マイプレイリスト {playlist} を再生して` |
| Play Podcast | `ポッドキャスト {podcast_name} を再生して` · `ポッドキャスト {podcast_name} を聴かせて` · `{podcast_name} の最新エピソードを再生して` · `ポッドキャスト {podcast_name} をスタートして` · `{podcast_name} ポッドキャストを聴きたい` |
| Play Radio | `ラジオを再生して` · `ラジオステーション {station} を再生して` · `ラジオモードを再生して` · `ラジオをスタートして` · `似たような音楽を再生して` · `似た曲を再生して` |
| Play Random | `ランダムな {media_type} を再生して` · `ランダムに何か再生して` · `ランダムな {genre} {media_type} を再生して` · `{media_type} をシャッフルして` · `ランダムな曲を再生して` · `ランダムな音楽を再生して` |
| Play Song | `{song} を再生して` · `{musician} の {song} を再生して` · `曲 {song} を再生して` · `{musician} の曲 {song} を再生して` · `{song} を聴かせて` · `{musician} の {song} を聴かせて` |
| Play Video | `ビデオ {title} を再生して` · `動画 {title} を流して` · `{title} を再生して` · `{title} を見たい` · `{title} を見せて` · `映画 {title} を再生して` |
| Query Artist Library | `{musician} のトラックは何がある` · `{musician} の {query_type} を見せて` · `{musician} の曲は何がある` · `{musician} のアルバムは何がある` · `{musician} には何がある` · `{musician} のトラックを見せて` |
| Query Recently Added | `新着はある` · `最近追加されたものは` · `ライブラリの新着は` · `最近追加されたものを見せて` · `最近何か新しいものある` · `最新のアイテムは何` |
| Recommend | `何かおすすめは` · `{media_type} をおすすめして` · `音楽のおすすめは` · `映画のおすすめは` · `何か見るものを提案して` |
| Search Media | `映画 {query} を検索して` · `コンテンツ {query} を検索して` · `ビデオ {query} を検索して` · `映画 {query} を見つけて` · `コンテンツ {query} を見つけて` · `映画 {query} を探して` |
| Set Reminder | `{duration_minutes} 分後にリマインドして` · `{reminder_time} にリマインドして` · `{duration_minutes} 分のリマインダーを設定して` · `{reminder_time} のリマインダーを設定して` |
| Show More | `もっと見せて` · `次のページ` · `もっと` · `続き` · `他に何がある` · `もっと結果` |
| Shuffle Play | `プレイリスト {playlist} をシャッフルで再生して` · `シャッフルでプレイリスト {playlist} を再生して` |
| Sleep Timer | `{duration_minutes} 分後に止めて` · `スリープタイマー {duration_minutes} 分` · `{duration_minutes} 分後におやすみタイマー` · `{duration_minutes} 分でスリープタイマーをセットして` |
| Turn Radio Off | `ラジオモードをオフにして` · `ラジオモードを無効にして` · `ラジオをオフにして` · `ラジオを無効にして` · `ラジオモードを止めて` |
| Turn Radio On | `ラジオモードをオンにして` · `ラジオモードを有効にして` · `ラジオをオンにして` · `ラジオを有効にして` |
| Unmark Favorite | `これ嫌い` · `ビデオ嫌い` · `曲嫌い` · `音楽嫌い` · `ビデオをお気に入りから削除して` · `曲をお気に入りから削除して` |
| Who Am I | `私は誰` · `どのアカウント` · `どのアカウントを使ってる` · `誰が話してる` · `どのプロフィールがアクティブ` |

### <a id="nl-nl"></a>Dutch (nl-NL)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `voeg {song} toe aan mijn wachtrij` · `voeg {song} van {musician} toe aan mijn wachtrij` · `voeg {song} toe aan de wachtrij` · `voeg {song} van {musician} toe aan de wachtrij` · `wachtrij {song}` · `wachtrij {song} van {musician}` |
| Browse Library | `{browse_category}` · `alleen {browse_category}` · `ik wil {browse_category}` · `browse {browse_category}` · `laat {browse_category} zien` · `lijst {browse_category}` |
| Clear Queue | `wis mijn wachtrij` · `wis de wachtrij` · `leeg mijn wachtrij` · `leeg de wachtrij` · `verwijder alles uit mijn wachtrij` |
| Continue Watching | `verder kijken` · `verder luisteren` · `hervat waar ik was gebleven` · `wat was ik aan het kijken` · `doorgaan` · `verder gaan` |
| Find Song | `find a song` · `find a song called {titleKeywords}` · `help me find a song` · `search for a song` · `I'm looking for a song` · `I need to find a song` |
| Find Song By Artist | `find a song by {musician}` · `help me find a song by {musician}` · `search for a song by {musician}` · `I'm looking for a song by {musician}` |
| Follow Me | `volg me` · `verder met afspelen` · `neem het over` · `doorgaan met luisteren` |
| Go To Chapter | `volgend hoofdstuk` · `ga naar hoofdstuk {chapter_number}` · `vorig hoofdstuk` · `spring naar hoofdstuk {chapter_number}` · `een hoofdstuk vooruit` · `een hoofdstuk terug` |
| In Progress Media List | `waar was ik naar aan het luisteren` · `waar was ik naar aan het kijken` · `wat is in behandeling` · `laat mijn voortgang zien` · `wat was ik aan het afspelen` · `wat heb ik gestart` |
| Learn My Voice | `leer mijn stem` · `onthoud mijn stem` · `herken mij` · `koppel mijn stem` · `dit is mijn stem` · `stel mijn stemprofiel in` |
| List Queue | `wat zit er in mijn wachtrij` · `wat zit er in de wachtrij` · `wat komt er aan` · `wat is het volgende` · `laat mijn wachtrij zien` · `wat wordt hierna afgespeeld` |
| Loop Song On | `loop dit nummer` · `herhaal dit nummer` · `zet dit nummer op repeat` · `blijf dit nummer herhalen` |
| Mark Favorite | `ik vind dit leuk` · `ik vind de video leuk` · `ik vind het nummer leuk` · `ik vind de muziek leuk` · `ik vind dit nummer leuk` · `voeg de video toe aan favorieten` |
| Media Info | `hoe heet dit nummer` · `wat is de {media_info_type} hiervan` · `hoe heet de video` · `hoe heet de muziek` · `wat is de titel van het nummer` · `wat speelt er nu` |
| Play Album | `speel het album {album}` · `speel het album {album} van {musician}` · `een album van {musician}` · `speel album {album}` · `luister naar het album {album}` · `speel een album van {musician}` |
| Play Artist Songs | `speel nummers van {musician}` · `speel muziek van {musician}` · `speel tracks van {musician}` · `speel liedjes van {musician}` · `luister naar {musician}` · `luister naar nummers van {musician}` |
| Play Book | `play {book}` · `play the book {book}` · `play audiobook {book}` · `play the audiobook {book}` · `listen to {book}` · `listen to the book {book}` |
| Play By Decade | `speel nummers uit de {decade}` · `speel {genre} uit de {decade}` · `speel hits uit de {decade}` · `speel muziek uit de {decade}` · `speel {decade} hits` · `speel {decade} nummers` |
| Play By Genre | `speel wat {genre} muziek` · `speel {genre} nummers` · `speel {genre} muziek` · `speel me wat {genre}` · `ik wil naar {genre} luisteren` · `speel {genre}` |
| Play Channel | `speel kanaal {channel}` · `speel radio {channel}` |
| Play Episode | `speel seizoen {season_number} aflevering {episode_number} van {series_name}` · `speel {series_name} seizoen {season_number} aflevering {episode_number}` · `kijk seizoen {season_number} aflevering {episode_number} van {series_name}` |
| Play Favorites | `speel mijn favoriete {media_type}` · `speel mijn favorieten` · `speel de favorieten van {username}` · `speel {username} favoriete {media_type}` · `speel mijn favoriete nummers` · `speel mijn favoriete muziek` |
| Play Last Added | `speel laatst toegevoegde {media_type}` · `speel nieuwe media` · `speel {media_type} toegevoegd {time_period}` · `speel recent toegevoegde {media_type}` · `speel iets nieuws` · `speel recent toegevoegde {media_type} van {time_period}` |
| Play Mood Music | `speel {mood} muziek` · `speel iets {mood}` · `speel {mood} nummers` · `ik wil {mood} muziek` · `speel mij iets {mood}` |
| Play Next | `speel {song} hierna` · `speel {song} van {musician} hierna` · `ik wil {song} hierna horen` · `speel {song} na dit` |
| Play Next Episode | `speel de volgende aflevering van {series_name}` · `speel de volgende aflevering van de serie {series_name}` · `speel de nieuwste aflevering van {series_name}` · `ga verder met {series_name}` |
| Play Playlist | `speel de afspeellijst {playlist}` · `speel mijn afspeellijst {playlist}` · `zet de afspeellijst {playlist} op` · `start de afspeellijst {playlist}` · `zet mijn afspeellijst {playlist} op` · `luister naar de afspeellijst {playlist}` |
| Play Podcast | `speel de podcast {podcast_name}` · `speel podcast {podcast_name}` · `luister naar de podcast {podcast_name}` · `speel de laatste aflevering van {podcast_name}` · `start de podcast {podcast_name}` · `ik wil naar podcast {podcast_name} luisteren` |
| Play Radio | `speel radio` · `start het radiostation {station}` · `speel radiomodus` · `start radio` · `speel meer zoals dit` · `blijf vergelijkbare muziek afspelen` |
| Play Random | `speel een willekeurig {media_type}` · `speel iets willekeurigs` · `speel een willekeurig {media_type} uit {genre}` · `speel willekeurige {genre} {media_type}` · `speel willekeurig {media_type}` · `shuffle mijn {media_type}` |
| Play Song | `speel {song}` · `speel {song} van {musician}` · `speel het nummer {song}` · `speel nummer {song}` · `speel het nummer {song} van {musician}` · `luister naar {song}` |
| Play Video | `speel de video {title}` · `zet de video {title} op` · `start {title}` · `kijk {title}` · `kun je {title} afspelen` · `ik wil {title} kijken` |
| Query Artist Library | `welke tracks hebben we van {musician}` · `welke {query_type} hebben we van {musician}` · `welke nummers hebben we van {musician}` · `welke albums hebben we van {musician}` · `wat hebben we van {musician}` · `laat tracks zien van {musician}` |
| Query Recently Added | `wat is er nieuw` · `wat is er recentelijk toegevoegd` · `wat is er nieuw in mijn bibliotheek` · `laat recent toegevoegde zien` · `iets nieuws onlangs` · `laat de nieuwste items zien` |
| Recommend | `beveel iets aan` · `beveel {media_type} aan` · `beveel wat muziek aan` · `beveel een film aan` · `stel iets voor om te kijken` · `stel wat muziek voor` |
| Search Media | `zoek naar een film {query}` · `zoek naar content {query}` · `zoek naar een video {query}` · `zoek naar een serie {query}` · `vind een film {query}` · `vind content {query}` |
| Set Reminder | `herinner me over {duration_minutes} minuten` · `herinner me om {reminder_time}` · `stel een herinnering in voor {duration_minutes} minuten` · `zet een herinnering op {reminder_time}` |
| Show More | `toon meer` · `meer resultaten` · `volgende` · `doorgaan` · `wat nog meer` · `meer` |
| Shuffle Play | `speel de playlist {playlist} in willekeurige volgorde` · `shuffle de playlist {playlist}` |
| Sleep Timer | `stop met afspelen over {duration_minutes} minuten` · `stel een slaaptimer in voor {duration_minutes} minuten` · `slaaptimer {duration_minutes} minuten` · `stop na {duration_minutes} minuten` · `zet uit over {duration_minutes} minuten` |
| Turn Radio Off | `zet radiomodus uit` · `schakel radiomodus uit` · `radiomodus uit` · `zet radio uit` · `schakel radio uit` · `stop radiomodus` |
| Turn Radio On | `zet radiomodus aan` · `schakel radiomodus in` · `radiomodus aan` · `zet radio aan` · `schakel radio in` |
| Unmark Favorite | `ik vind dit niet leuk` · `ik vind de video niet leuk` · `ik vind het nummer niet leuk` · `verwijder de video uit favorieten` · `verwijder het nummer uit favorieten` |
| Who Am I | `wie ben ik` · `welk account is dit` · `welk account gebruik ik` · `wie spreekt er` · `welk profiel is actief` · `ben ik herkend` |

### <a id="pt-br"></a>Portuguese - Brazil (pt-BR)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `adicionar {song} à minha fila` · `adicionar {song} de {musician} à minha fila` · `adicionar {song} à fila` · `adicionar {song} de {musician} à fila` · `enfileirar {song}` · `enfileirar {song} de {musician}` |
| Browse Library | `{browse_category}` · `só {browse_category}` · `quero {browse_category}` · `navegar {browse_category}` · `mostrar {browse_category}` · `listar {browse_category}` |
| Clear Queue | `limpar minha fila` · `limpar a fila` · `esvaziar minha fila` · `esvaziar a fila` · `remover tudo da minha fila` · `limpar minha playlist` |
| Continue Watching | `continuar assistindo` · `continuar ouvindo` · `retomar de onde parei` · `o que eu estava assistindo` · `continuar tocando` · `continuar` |
| Find Song | `encontre uma musica` · `encontre uma musica chamada {titleKeywords}` · `me ajude a encontrar uma musica` · `estou procurando uma musica` · `procure uma musica chamada {titleKeywords}` · `procure uma musica` |
| Find Song By Artist | `encontre uma musica de {musician}` · `me ajude a encontrar uma musica de {musician}` · `estou procurando uma musica de {musician}` |
| Follow Me | `me siga` · `continuar tocando` · `retomar a reprodução` · `transferir a música` |
| Go To Chapter | `próximo capítulo` · `ir para o capítulo {chapter_number}` · `capítulo anterior` · `pular para o capítulo {chapter_number}` · `avançar um capítulo` · `voltar um capítulo` |
| In Progress Media List | `o que eu estava ouvindo` · `o que eu estava assistindo` · `o que está em andamento` · `mostrar meu progresso` · `o que eu estava tocando` · `listar mídias em andamento` |
| Learn My Voice | `aprender minha voz` · `lembrar minha voz` · `me reconhecer` · `vincular minha voz` · `essa é minha voz` · `configurar meu perfil de voz` |
| List Queue | `o que tem na minha fila` · `o que tem na fila` · `o que vem aí` · `o que vem depois` · `mostrar minha fila` · `listar minha fila` |
| Loop Song On | `repetir esta música` · `colocar esta música em loop` · `repetir a música` · `repetir essa música para sempre` · `loop nesta música` |
| Mark Favorite | `eu gostei` · `eu gostei do vídeo` · `eu gostei da música` · `adicionar o vídeo aos favoritos` · `adicionar a música aos favoritos` · `salvar nos favoritos` |
| Media Info | `qual é o nome da música` · `qual {media_info_type} é essa` · `qual é o nome do vídeo` · `qual é o título da música` · `o que está tocando` · `me diga a {media_info_type}` |
| Play Album | `tocar o álbum {album}` · `tocar o álbum {album} de {musician}` · `um álbum de {musician}` · `tocar álbum {album}` · `ouvir o álbum {album}` · `tocar um álbum de {musician}` |
| Play Artist Songs | `tocar músicas de {musician}` · `tocar música de {musician}` · `tocar faixas de {musician}` · `ouvir {musician}` · `ouvir músicas de {musician}` · `ouvir música de {musician}` |
| Play Book | `tocar {book}` · `tocar o livro {book}` · `ouvir {book}` · `ouvir o livro {book}` · `ouvir o audiolivro {book}` |
| Play By Decade | `tocar músicas dos {decade}` · `tocar {genre} dos {decade}` · `tocar faixas dos {decade}` · `tocar hits dos {decade}` · `tocar música dos {decade}` · `tocar {decade} hits` |
| Play By Genre | `tocar música {genre}` · `tocar músicas {genre}` · `tocar {genre}` · `quero ouvir {genre}` · `me dá música {genre}` · `colocar um {genre}` |
| Play Channel | `tocar canal {channel}` · `tocar rádio {channel}` |
| Play Episode | `tocar temporada {season_number} episódio {episode_number} de {series_name}` · `tocar {series_name} temporada {season_number} episódio {episode_number}` · `assistir temporada {season_number} episódio {episode_number} de {series_name}` · `assistir {series_name} temporada {season_number} episódio {episode_number}` |
| Play Favorites | `tocar meus favoritos` · `tocar {media_type} favoritos` · `tocar os favoritos de {username}` · `tocar {media_type} favoritos de {username}` · `tocar minhas músicas favoritas` · `tocar minha música favorita` |
| Play Last Added | `tocar últimos {media_type} adicionados` · `tocar mídias novas` · `tocar {media_type} adicionados {time_period}` · `tocar {media_type} adicionados recentemente` · `tocar algo novo` · `tocar {media_type} novos de {time_period}` |
| Play Mood Music | `tocar música {mood}` · `tocar algo {mood}` · `tocar músicas {mood}` · `quero música {mood}` |
| Play Next | `tocar {song} depois` · `tocar {song} de {musician} depois` · `quero ouvir {song} depois` · `tocar {song} a seguir` · `tocar {song} de {musician} a seguir` · `tocar {song} após essa` |
| Play Next Episode | `tocar o próximo episódio de {series_name}` · `tocar o próximo episódio da série {series_name}` · `tocar o episódio mais recente de {series_name}` · `continuar assistindo {series_name}` |
| Play Playlist | `tocar a playlist {playlist}` · `tocar minha playlist {playlist}` · `colocar a playlist {playlist}` · `iniciar a playlist {playlist}` · `ouvir a playlist {playlist}` · `pode tocar a playlist {playlist}` |
| Play Podcast | `tocar o podcast {podcast_name}` · `tocar podcast {podcast_name}` · `ouvir o podcast {podcast_name}` · `ouvir podcast {podcast_name}` · `tocar o último episódio de {podcast_name}` · `iniciar o podcast {podcast_name}` |
| Play Radio | `tocar rádio` · `Toque a estação de rádio {station}` · `tocar modo rádio` · `iniciar rádio` · `tocar mais como esse` · `continuar tocando música similar` |
| Play Random | `tocar um {media_type} aleatório` · `tocar algo aleatório` · `tocar um {media_type} aleatório de {genre}` · `embaralhar {genre} {media_type}` · `tocar {genre} aleatório` · `tocar {media_type} aleatório` |
| Play Song | `tocar {song}` · `tocar {song} de {musician}` · `tocar a música {song}` · `tocar música {song}` · `tocar a música {song} de {musician}` · `ouvir {song}` |
| Play Video | `tocar o vídeo {title}` · `colocar o vídeo {title}` · `começar a tocar {title}` · `assistir {title}` · `pode tocar {title}` · `quero assistir {title}` |
| Query Artist Library | `quais faixas temos de {musician}` · `quais {query_type} temos de {musician}` · `quais músicas temos de {musician}` · `quais álbuns temos de {musician}` · `o que temos de {musician}` · `mostrar faixas de {musician}` |
| Query Recently Added | `o que há de novo` · `o que foi adicionado recentemente` · `o que há de novo na minha biblioteca` · `mostrar adicionados recentemente` · `alguma novidade` · `mostrar os itens mais recentes` |
| Recommend | `recomendar algo` · `recomendar {media_type}` · `recomendar uma música` · `recomendar um filme` · `sugerir algo para assistir` · `sugerir uma música` |
| Search Media | `procurar um filme {query}` · `procurar conteúdo {query}` · `procurar um vídeo {query}` · `procurar uma série {query}` · `encontrar um filme {query}` · `encontrar conteúdo {query}` |
| Set Reminder | `me lembre em {duration_minutes} minutos` · `me lembre às {reminder_time}` · `crie um lembrete de {duration_minutes} minutos` · `crie um lembrete para as {reminder_time}` |
| Show More | `mostrar mais` · `mais resultados` · `próximo` · `continuar` · `o que mais` · `ver mais` |
| Shuffle Play | `toque a playlist {playlist} em modo aleatório` · `embaralhe a playlist {playlist}` |
| Sleep Timer | `parar de tocar em {duration_minutes} minutos` · `definir timer de sono para {duration_minutes} minutos` · `timer de sono {duration_minutes} minutos` · `parar após {duration_minutes} minutos` · `desligar em {duration_minutes} minutos` · `definir timer de sono {duration_minutes}` |
| Turn Radio Off | `desativar modo rádio` · `desabilitar modo rádio` · `modo rádio desligado` · `desligar rádio` · `desabilitar rádio` · `parar modo rádio` |
| Turn Radio On | `ativar modo rádio` · `habilitar modo rádio` · `modo rádio ligado` · `ligar rádio` · `habilitar rádio` |
| Unmark Favorite | `eu não gostei disso` · `eu não gostei do vídeo` · `eu não gostei da música` · `remover o vídeo dos favoritos` · `remover a música dos favoritos` · `tirar dos favoritos` |
| Who Am I | `quem sou eu` · `qual conta é essa` · `qual conta estou usando` · `quem está falando` · `qual perfil está ativo` · `estou sendo reconhecido` |
