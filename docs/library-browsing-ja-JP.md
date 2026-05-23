# ライブラリ閲覧 (ja-JP)

```mermaid
graph TD
    Idle["アイドル"] --> Browse["BrowseLibraryIntent<br/>{browse_category} をブラウズして<br/>どんな {browse_category} がある"]]

    Browse -->|"アーティストをブラウズ"| Artists["アーティスト一覧"]
    Browse -->|"アルバムをブラウズ"| Albums["アルバム一覧"]
    Browse -->|"曲をブラウズ"| Songs["曲一覧"]
    Browse -->|"映画をブラウズ"| Movies["映画一覧"]
    Browse -->|"シリーズをブラウズ"| Series["シリーズ一覧"]
    Browse -->|"本をブラウズ"| Books["本一覧"]
    Browse -->|"ジャンルをブラウズ"| Genres["ジャンル一覧"]

    Artists --> Selection["ユーザーがアイテムを選択"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["選択したアイテムを再生"]

    Idle --> Favorites["PlayFavoritesIntent<br/>お気に入りを再生して"]
    Favorites --> PlayFav["お気に入りプレイリストを再生"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>何聴いてたっけ"]
    RecentlyPlayed --> ProgressList["再生中のメディア一覧"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>新しいメディアを再生して"]
    LastAdded --> RecentList["最近追加されたアイテム"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>新着はある？"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>何かおすすめは？"]
    Recommend --> RecList["おすすめアイテム"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>play {book}"]
    PlayBook --> Audiobook["オーディオブック再生"]
    Audiobook -->|"次のチャプター"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"前のチャプター"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"チャプター{n}に移動"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>ポッドキャスト {podcast_name} を再生して"]
    PlayPodcast --> PodcastPlay["ポッドキャスト再生"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>続きを見て"]
    ContinueWatch --> ResumePlayback["再生中のメディアを再開"]

    Idle --> SearchMedia["SearchMediaIntent<br/>{query} を検索して"]
    SearchMedia --> SearchResults["検索結果"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
