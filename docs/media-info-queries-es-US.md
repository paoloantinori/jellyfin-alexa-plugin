# Consultas de Informacion Multimedia (es-US)

```mermaid
graph TD
    Playing["En reproduccion"] --> GenericQ["Consultas genericas"]
    Playing --> SlotQ["Consultas por tipo de slot"]

    GenericQ -->|"Que esta sonando"| TitleResp["Responde con el titulo"]
    GenericQ -->|"que estoy escuchando"| TitleResp
    GenericQ -->|"que esta sonando ahora"| TitleResp
    GenericQ -->|"que estoy viendo"| TitleResp
    GenericQ -->|"que es esto"| TitleResp
    GenericQ -->|"dame informacion de esta cancion"| FullInfo["Respuesta info completa"]
    GenericQ -->|"informacion sobre este contenido"| FullInfo

    SlotQ -->|"Que {media_info_type} es"| SlotResp["Respuesta especifica por slot"]
    SlotQ -->|"Dime la {media_info_type}"| SlotResp
    SlotQ -->|"Cual es la {media_info_type}"| SlotResp

    SlotQ -->|"cual es el nombre de esta cancion /<br/>que cancion es esta"| Title["titulo"]
    SlotQ -->|"de que album es"| Album["album"]
    SlotQ -->|"Quien canta /<br/>quien canta esta cancion"| Artist["artista"]
    SlotQ -->|"que ano es /<br/>cuando se lanzo"| Year["ano"]
    SlotQ -->|"Cuanto dura /<br/>cuanto dura esta cancion"| Duration["duracion"]
    SlotQ -->|"Que genero es"| Genre["genero"]
    SlotQ -->|"Cuentame sobre el artista /<br/>Habla del artista"| Biography["biografia"]
    SlotQ -->|"quien dirigió esto /<br/>quien es el director"| Director["director"]
    SlotQ -->|"quien actua en esto /<br/>quienes son los actores"| Cast["reparto"]
    SlotQ -->|"que temporada es esta /<br/>que temporada es"| Season["temporada"]
    SlotQ -->|"que episodio es este /<br/>que episodio es"| Episode["episodio"]
    SlotQ -->|"que serie es esta"| Series["serie"]
    SlotQ -->|"quien es el autor /<br/>quien escribio esto"| Author["autor"]
    SlotQ -->|"quien es el narrador /<br/>quien narra esto"| Narrator["narrador"]
    SlotQ -->|"cual es la calificacion /<br/>como esta calificado"| Rating["calificacion"]

    TitleResp --> Playing
    FullInfo --> Playing
    SlotResp --> Playing
    Title --> Playing
    Album --> Playing
    Artist --> Playing
    Year --> Playing
    Duration --> Playing
    Genre --> Playing
    Biography --> Playing
    Director --> Playing
    Cast --> Playing
    Season --> Playing
    Episode --> Playing
    Series --> Playing
    Author --> Playing
    Narrator --> Playing
    Rating --> Playing

    style Playing fill:#4CAF50,color:#fff
    style GenericQ fill:#2196F3,color:#fff
    style SlotQ fill:#FF9800,color:#fff
```
