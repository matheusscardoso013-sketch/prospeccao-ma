# ProspeccaoMA.Web — plataforma de originação sell-side da Valore Brasil

Prospecta diariamente empresas de middle market a partir da base pública da Receita
Federal, lê o site oficial de cada uma para saber o que ela faz, e cruza esse perfil
com a tese de investimento de ~245 compradores. Entrega uma lista pontuada de 0 a 100
com racional escrito.

No ar em https://prospeccao-ma.onrender.com

## Onde cada coisa vive

Nada disso está no Claude — trocar de conta ou de máquina não afeta a plataforma.

| Peça | Serviço | Observação |
|---|---|---|
| Código | GitHub `matheusscardoso013-sketch/prospeccao-ma` | deploy automático no push para `main` |
| Aplicação | Render (free tier) | hiberna após ~15 min SEM requisição de entrada |
| Banco | Neon (Postgres serverless) | derruba conexão ociosa — daí o `EnableRetryOnFailure` |
| IA | Google Gemini (cota gratuita) | ~20 requisições/dia POR MODELO |
| E-mail | Brevo, via API HTTP (porta 443) | o Render bloqueia portas SMTP |
| Vigia | GitHub Actions `.github/workflows/rodada-diaria.yml` | roda 12:05 BRT, independente de qualquer máquina |

Segredos ficam em variáveis de ambiente no Render, nunca no repositório:
`ConnectionStrings:Neon`, `Gemini:ApiKey`, `Email:ApiKey`, `Prospeccao:TokenJob`.

## Como rodar

```
dotnet run --project src/ProspeccaoMA.Web                      # sobe o site
dotnet run --project src/ProspeccaoMA.Web -- status            # panorama completo
dotnet run --project src/ProspeccaoMA.Web -- ultimos           # últimos leads e seus matches
dotnet run --project src/ProspeccaoMA.Web -- qualidade         # auditoria do matching
```

Comandos de console ficam registrados no topo do `Program.cs`. Os principais para o
dia a dia: `cadastrar-comprador`, `cruzar-comprador`, `derivar-sites`, `fila`,
`modelos --testar`, `recorte`, `duplicados`.

Comprador novo saído de reunião:
```
dotnet run --project src/ProspeccaoMA.Web -- cadastrar-comprador tese.json --gravar
dotnet run --project src/ProspeccaoMA.Web -- cruzar-comprador "Nome" --cnae 62,63 --enriquecer 25 --gravar
```

## O que a rodada diária faz (12h)

`JobProspeccaoService` → `RotinaProspeccao.ExecutarAsync`:

1. seleciona até 12 empresas novas dentro do recorte (UF, CNAE, capital social)
2. `EnriquecerAsync` — contatos via BrasilAPI
3. `DescreverAsync` — **lê o site oficial e grava o que a empresa faz**
4. `ClassificarAsync` — pontua o lead contra a configuração
5. `MotorSinergia.CruzarLeadAsync` — pontua o lead contra os compradores aderentes
6. reprocessa falhas antigas, drena "dado rico" com a cota que sobrou, envia o resumo

O passo 3 entrou em 17/08 e foi o maior salto de qualidade do projeto. Antes dele a IA
julgava a empresa sabendo só razão social, CNAE e capital — e "Portais, provedores de
conteúdo" descreve milhares de empresas de forma idêntica. Daí vinham os matches
absurdos (marketplace de moda casando com Itaú e B3).

## Restrições aprendidas na marra

Cada uma destas custou dias para descobrir. Não as reaprenda.

- **Cota do Gemini é por modelo, não global.** A rotação de 8 modelos só avança no 429,
  então os últimos da lista são RESERVA, não capacidade imediata.
- **Embeddings têm cota própria e FINITA** (~1.000/dia). Não são de graça.
- **Render hiberna sem requisição de ENTRADA.** Trabalho em segundo plano não conta —
  por isso o vigia fica pingando durante a rodada, que passa de 15 min.
- **A rodada precisa se auto-recuperar.** Em 19/07 a plataforma ficou 9 dias parada em
  silêncio porque o gatilho era externo. Hoje todo despertar do app confere se a rodada
  do dia saiu.
- **`/Jobs/Saude` responder 200 não significa que o time consegue entrar.** Em 14/08 o
  login devolvia 500 com a saúde em 200 — o login é a única tela que monta o key ring
  de Data Protection. O vigia confere as duas portas.
- **Middle market é o recorte, com teto.** Capital social de R$ 500 mil a R$ 20 milhões.
  Empresas grandes e líderes de setor não transacionam — foi feedback direto do negócio.
- **Faturamento alvo definido pelo negócio:** R$ 4,8 milhões a R$ 200 milhões.
- **Site derivado do e-mail exige checagem de dono** (`ComandoDerivarSites.Combina`). É
  praxe no Brasil cadastrar o e-mail do CONTADOR na Receita. Sem a checagem, a plataforma
  descreveria o escritório de contabilidade como se fosse o alvo, com confiança. Preferimos
  perder metade dos domínios a acertar o alvo errado — **não afrouxe essa regra.**
- **Score 0 nunca é gravado como par.** Uma linha com score 0 conta como "já cruzado" e o
  lead nunca mais voltaria.
- **Windows Smart App Control** bloqueia DLLs locais não assinadas. Use `dotnet run`, não
  `dotnet <caminho>.dll`. Pare o servidor antes de compilar (ele trava o .exe).

## Estilo do repositório

Mensagens de commit são narrativas: explicam o PROBLEMA e por que a solução é essa, não
só o que mudou. São a memória do projeto — vale ler `git log` antes de mexer em algo que
parece estranho, porque normalmente não é.

Comentários no código seguem a mesma regra: explicam a razão, com data e número quando
houve medição.

## Em aberto

- O funil não registra abordagem desde 30/07 — as oportunidades quentes dependem do time.
- 224 de 245 critérios de tese extraídos pela IA, **0 validados** pelo time.
- 21 compradores sem tese cadastrada.
- SPF/DKIM/DMARC no `valorebrasil.com.br` pendentes: o e-mail ainda sai de remetente genérico.
- Causa raiz do 500 no login de 14/08 não fechada — precisa dos logs do Render.
