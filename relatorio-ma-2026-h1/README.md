# M&A Deals Report Brasil — 1º semestre de 2026

Landing page de captura + relatório, no modelo do material da Questum, com dados
do mercado brasileiro de M&A no H1 2026 e identidade visual da Valore Brasil.

## Estrutura

```
relatorio-ma-2026-h1/
  site/                                    ← ARTEFATO PÚBLICO (é esta pasta que vai para o host)
    index.html                             landing page de captura
    relatorio.html                         relatório em HTML (tela + impressão A4)
    Valore-MA-Deals-Report-2026-H1.pdf     PDF de 19 páginas entregue no formulário
    _headers                               cabeçalhos de segurança (Netlify / Cloudflare Pages)
    robots.txt
  deals-h1-2026.json                       base estruturada — INTERNO, não publicar
  README.md                                este arquivo — INTERNO, não publicar
```

**Publique apenas `site/`.** O JSON e este README ficam de fora de propósito: o
README descreve pendências internas e não deve ficar acessível em `/README.md`.

Sem dependência externa: nada de CDN, fonte remota ou framework. É HTML estático.

## Pôr no ar (host estático)

Escolhido: host estático dedicado, para desvincular o material comercial do
repositório da plataforma.

**Cloudflare Pages** ou **Netlify** — os dois aceitam arrastar a pasta:

1. Crie a conta no serviço (essa parte é sua — não crio contas).
2. Novo projeto → opção de **deploy manual / drag-and-drop**.
3. Arraste a pasta **`site/`** (não a pasta de cima).
4. O serviço devolve um endereço provisório (`algo.pages.dev` ou `algo.netlify.app`).
5. **Custom domain** → `lp.valorebrasil.com.br`, e crie o CNAME correspondente no
   DNS da Valore apontando para o endereço que o serviço indicar.

Alternativa versionada: existe `.github/workflows/publicar-relatorio.yml`, que
publica `site/` no GitHub Pages a cada push na `main`. Exige ligar
`Settings → Pages → Source: GitHub Actions` uma vez. Só vale a pena se você quiser
o material no mesmo repositório da plataforma.

## Pendências antes de divulgar

**1. O formulário não grava lead nenhum.** Ele valida os campos, entrega o PDF por
download direto e registra o payload no console. Nenhum lead é salvo e nenhum
e-mail é disparado.

Para ativar, edite o `<script>` no fim de `site/index.html`:

```js
var ENDPOINT = null;   // ← troque pela URL do destino
```

O `POST` vai em JSON:

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
esperar `application/x-www-form-urlencoded`, ajuste o `fetch`.

A página **já funciona sem isso** — o visitante recebe o relatório pelo botão de
download. O que falta é a captação. A frase "também enviamos uma cópia para o seu
e-mail" só aparece quando o `ENDPOINT` está configurado; sem sistema de envio ela
seria falsa.

**2. Depois de definir o domínio,** acrescente ao `<head>` de `index.html`:

- `<meta property="og:url">` com o endereço final
- `<meta property="og:image">` com uma imagem de compartilhamento (sem ela, o link
  no WhatsApp e no LinkedIn aparece sem miniatura — importante, porque é por ali
  que esse material circula)
- `sitemap.xml`, se quiser ajudar a indexação

## Regenerar o PDF

Depois de editar `site/relatorio.html`:

```bash
python -m http.server 5099 --directory relatorio-ma-2026-h1/site
```

E, em outro terminal:

```bash
"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless --disable-gpu --no-pdf-header-footer --print-to-pdf="relatorio-ma-2026-h1/site/Valore-MA-Deals-Report-2026-H1.pdf" "http://localhost:5099/relatorio.html"
```

O CSS de impressão já cuida de A4, margens, quebras de página e supressão de
efeitos de tela. Não há Node nesta máquina — daí o Chrome headless em vez de
ferramentas em JS.

## Preview local

Configuração pronta em `.claude/launch.json` chamada `relatorio-ma` (serve `site/`
na porta 5099).

## Regra editorial da base

Nenhuma transação entrou sem fonte pública citável. Valor não divulgado está como
`n/d` e **não foi estimado** — 12 das 27 operações estão nessa condição. Três
transações foram descartadas na checagem e ficam registradas em
`deals-h1-2026.json` e no próprio relatório, com o motivo: uma era de 2025, uma
tinha valor sem confirmação suficiente e uma vinha de fonte única de baixa
confiabilidade.

Se o relatório for atualizado, manter essa regra — é o que separa material de
autoridade de conteúdo de volume.

## Fontes dos agregados

- **TTR Data** — *Relatório trimestral sobre o mercado transacional brasileiro, 2Q26*
- **Kroll** — *Brazil Transactions Insights – Summer 2026* (via Money Report)
- **Questum** — *M&A Deals Report 2026 H1* (via Startups.com.br), recorte de tecnologia
- Operações individuais: Bloomberg Línea, InfoMoney, Finsiders Brasil, TELETIME,
  Startups.com.br, Exame, CNN Brasil e comunicados das companhias.
