# تصفح المكتبة (ar-SA)

```mermaid
graph TD
    Idle["خامل"] --> Browse["BrowseLibraryIntent<br/>تصفح {browse_category}<br/>ما {browse_category} لدي"]]

    Browse -->|"تصفح الفنانين"| Artists["قائمة الفنانين"]
    Browse -->|"تصفح الألبومات"| Albums["قائمة الألبومات"]
    Browse -->|"تصفح الأغاني"| Songs["قائمة الأغاني"]
    Browse -->|"تصفح الأفلام"| Movies["قائمة الأفلام"]
    Browse -->|"تصفح المسلسلات"| Series["قائمة المسلسلات"]
    Browse -->|"تصفح الكتب"| Books["قائمة الكتب"]
    Browse -->|"تصفح الأنواع"| Genres["قائمة الأنواع"]

    Artists --> Selection["يختار المستخدم عنصراً"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["تشغيل العنصر المحدد"]

    Idle --> Favorites["PlayFavoritesIntent<br/>شغل مفضلاتي"]
    Favorites --> PlayFav["تشغيل قائمة المفضلات"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>ماذا كنت أستمع"]
    RecentlyPlayed --> ProgressList["قائمة الوسائط الجارية"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>شغل آخر ما أضيف من أغاني"]
    LastAdded --> RecentList["العناصر المضافة مؤخراً"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>ما الجديد"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>أوصني بشيء"]
    Recommend --> RecList["عناصر موصى بها"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>play {book}"]
    PlayBook --> Audiobook["تشغيل الكتاب الصوتي"]
    Audiobook -->|"الفصل التالي"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"الفصل السابق"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"انتقل إلى الفصل {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>شغل البودكاست {podcast_name}"]
    PlayPodcast --> PodcastPlay["تشغيل البودكاست"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>أكمل المشاهدة"]
    ContinueWatch --> ResumePlayback["استئناف الوسائط الجارية"]

    Idle --> SearchMedia["SearchMediaIntent<br/>ابحث عن {query}"]
    SearchMedia --> SearchResults["نتائج البحث"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
