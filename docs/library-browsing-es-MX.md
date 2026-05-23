# Navegacion de Biblioteca (es-MX)

```mermaid
graph TD
    Idle["Inactivo"] --> Browse["BrowseLibraryIntent<br/>explorar {browse_category}<br/>qué {browse_category} hay"]]

    Browse -->|"explorar artistas"| Artists["Lista de artistas"]
    Browse -->|"muestrame albums"| Albums["Lista de albums"]
    Browse -->|"lista canciones"| Songs["Lista de canciones"]
    Browse -->|"muestrame peliculas"| Movies["Lista de peliculas"]
    Browse -->|"explorar series"| Series["Lista de series"]
    Browse -->|"muestrame libros"| Books["Lista de libros"]
    Browse -->|"lista generos"| Genres["Lista de generos"]

    Artists --> Selection["El usuario elige un elemento"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Reproducir elemento seleccionado"]

    Idle --> Favorites["PlayFavoritesIntent<br/>Reproduce mis favoritos"]
    Favorites --> PlayFav["Reproducir playlist de favoritos"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>que estoy escuchando"]
    RecentlyPlayed --> ProgressList["Lista de contenidos en curso"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>Reproduce contenidos nuevos"]
    LastAdded --> RecentList["Elementos anadidos recientemente"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>que hay de nuevo"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>recomienda algo"]
    Recommend --> RecList["Elementos recomendados"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>reproduce {book}"]
    PlayBook --> Audiobook["Reproduccion de audiolibro"]
    Audiobook -->|"Siguiente capitulo"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"Capitulo anterior"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"Ir al capitulo {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>reproduce el podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Reproduccion de podcast"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>Continuar viendo"]
    ContinueWatch --> ResumePlayback["Reanudar contenido en curso"]

    Idle --> SearchMedia["SearchMediaIntent<br/>Busca {query}"]
    SearchMedia --> SearchResults["Resultados de busqueda"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
