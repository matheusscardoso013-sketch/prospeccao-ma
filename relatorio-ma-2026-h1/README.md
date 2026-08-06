# M&A Deals Report Brasil — 1º semestre de 2026

Landing page de captura + relatório, no modelo do material da Questum, com dados
do mercado brasileiro de M&A no H1 2026 e identidade visual da Valore Brasil.

## Arquivos

| Arquivo | O que é |
|---|---|
| `index.html` | Landing page de captura. HTML estático autocontido — publicável em qualquer host. |
| `relatorio.html` | O relatório. Otimizado para leitura em tela **e** para impressão em A4. |
| `Valore-MA-Deals-Report-2026-H1.pdf` | PDF do relatório (19 páginas), gerado a partir do `relatorio.html`. |
| `deals-h1-2026.json` | Base estruturada: 27 transações com fonte, agregados de mercado e registro do que foi descartado na checagem. |

Não há dependência externa: sem CDN, sem fonte remota, sem framework. Basta servir
os arquivos estáticos.

## Antes de publicar: ativar o formulário

**O formulário não está enviando dados para lugar nenhum.** Hoje ele valida os
campos e mostra a mensagem de sucesso localmente, registrando o payload no console.
Se a página for publicada assim, nenhum lead é capturado.

Para ativar, edite o bloco `<script>` no fim de `index.html`:

```js
var ENDPOINT = null;   // ← troque pela URL do destino
```

O `POST` vai em JSON com este corpo:

```json
{
  "nome": "...",
  "email": "...",
  "empresa": "...",
  "perfil": "vender | comprar | valuation | mercado | outro",
  "material": "ma-deals-report-2026-h1",
  "origem": "https://..."
}
```

Serve para RD Station, HubSpot, Formspree ou endpoint próprio. Se o serviço
esperar `application/x-www-form-urlencoded` em vez de JSON, ajuste o `fetch`.

Falta também **hospedar o PDF e decidir a entrega**: hoje a mensagem de sucesso
diz que o relatório foi enviado por e-mail. Ou você dispara o e-mail pelo
serviço de automação, ou troca o texto por um link direto para o PDF.

## Regerar o PDF

Depois de editar `relatorio.html`:

```bash
python -m http.server 5099 --directory relatorio-ma-2026-h1
```

E, em outro terminal:

```bash
"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless --disable-gpu --no-pdf-header-footer --print-to-pdf="relatorio-ma-2026-h1/Valore-MA-Deals-Report-2026-H1.pdf" "http://localhost:5099/relatorio.html"
```

O CSS de impressão já cuida de A4, margens, quebras de página e supressão de
efeitos de tela.

## Preview local

Há uma configuração pronta em `.claude/launch.json` chamada `relatorio-ma`
(serve esta pasta na porta 5099).

## Regra editorial da base

Nenhuma transação entrou sem fonte pública citável. Valor não divulgado está
como `n/d` e **não foi estimado** — 12 das 27 operações estão nessa condição.
Três transações foram descartadas na checagem e ficam registradas no JSON e no
próprio relatório, com o motivo: uma era de 2025, uma tinha valor sem
confirmação suficiente e uma vinha de fonte única de baixa confiabilidade.

Se o relatório for atualizado, manter essa regra — é o que separa material de
autoridade de conteúdo de volume.

## Fontes dos agregados

- **TTR Data** — *Relatório trimestral sobre o mercado transacional brasileiro, 2Q26*
- **Kroll** — *Brazil Transactions Insights – Summer 2026* (via Money Report)
- **Questum** — *M&A Deals Report 2026 H1* (via Startups.com.br), recorte de tecnologia
- Operações individuais: Bloomberg Línea, InfoMoney, Finsiders Brasil, TELETIME,
  Startups.com.br, Exame, CNN Brasil e comunicados das companhias.
