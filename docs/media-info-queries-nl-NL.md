# Media-infoquery's (nl-NL)

```mermaid
graph TD
    Playing["Wordt afgespeeld"] --> GenericQ["Algemene query's"]
    Playing --> SlotQ["Slotspecifieke query's"]

    GenericQ -->|"wat speelt er"| TitleResp["Reageert met titel"]
    GenericQ -->|"waar luister ik naar"| TitleResp
    GenericQ -->|"wat speelt er nu"| TitleResp
    GenericQ -->|"waar kijk ik naar"| TitleResp
    GenericQ -->|"wat is dit"| TitleResp
    GenericQ -->|"vertel me hierover"| FullInfo["Volledige media-info-reactie"]
    GenericQ -->|"informatie hierover"| FullInfo

    SlotQ -->|"Welke {media_info_type} is dit"| SlotResp["Slotspecifieke reactie"]
    SlotQ -->|"Vertel me de {media_info_type}"| SlotResp
    SlotQ -->|"Wat is de {media_info_type}"| SlotResp

    SlotQ -->|"Wat is de titel /<br/>Hoe heet dit nummer"| Title["titel"]
    SlotQ -->|"Van welk album is dit"| Album["album"]
    SlotQ -->|"Wie zingt dit /<br/>Wie voert dit uit"| Artist["artiest"]
    SlotQ -->|"In welk jaar is dit uitgebracht"| Year["jaar"]
    SlotQ -->|"Hoe lang duurt dit /<br/>Hoe lang is dit nummer"| Duration["duur"]
    SlotQ -->|"Welk genre is dit"| Genre["genre"]
    SlotQ -->|"Vertel me over deze artiest"| Biography["biografie"]
    SlotQ -->|"wie heeft dit geregisseerd /<br/>wie is de regisseur"| Director["regisseur"]
    SlotQ -->|"wie spelen hierin mee /<br/>wie zit hierin"| Cast["cast"]
    SlotQ -->|"welk seizoen is dit /<br/>welk seizoen is dit"| Season["seizoen"]
    SlotQ -->|"welke aflevering is dit /<br/>welke aflevering is dit"| Episode["aflevering"]
    SlotQ -->|"welke serie is dit"| Series["serie"]
    SlotQ -->|"wie is de auteur /<br/>wie heeft dit geschreven"| Author["auteur"]
    SlotQ -->|"wie is de verteller /<br/>wie vertelt dit"| Narrator["verteller"]
    SlotQ -->|"wat is de beoordeling /<br/>hoe is dit beoordeeld"| Rating["beoordeling"]

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
