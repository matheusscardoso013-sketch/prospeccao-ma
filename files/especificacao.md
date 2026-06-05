# Originação de Deals para M&A — Especificação Técnica

**Plataforma de mapeamento, classificação e priorização automática de targets.**
Documento-base para desenvolvimento assistido por Claude Code.

Stack: ASP.NET Core 8 (modular monolith) · SQL Server · Entity Framework Core · Ollama (IA local) · ASP.NET Core Identity · Custo recorrente zero.

---

## 0. Como usar este documento

Esta é a especificação acionável que você (Claude Code) deve seguir para construir a plataforma. Onde o detalhe é crítico (modelo de dados, scoring, classificação por IA, ingestão, autenticação) a especificação é precisa; onde é trivial (telas CRUD, plumbing), descreve a intenção e deixa você resolver.

Convenções: trechos em bloco de código são contratos a implementar literalmente (nomes de tabelas, campos JSON, assinaturas de interface). "Deve" = requisito; "pode"/"sugere-se" = recomendação ajustável.

**Premissas inegociáveis:**

1. **Custo recorrente zero** — toda fonte de dados e ferramenta deve ser gratuita.
2. **IA local via Ollama** — sem chamadas pagas a APIs de IA.
3. **Mapeamento automático diário** — sem intervenção manual.
4. **Plataforma online** — acessível pela internet, multiusuário, com login.
5. **Rigor de fonte** — todo dado carrega origem e data; estimativas marcadas com "~".
6. **Conformidade com a LGPD** — apenas dados públicos.

---

## 1. Objetivo e escopo

Substituir uma originação dependente de relacionamento e indicação por um processo sistemático e auditável de mapeamento de targets. A plataforma cruza setores prioritários, faixa de faturamento e eventos de gatilho; classifica oportunidades automaticamente via IA local; e as organiza num funil visual (kanban), liberando os sócios para relacionamento e negociação.

**Dentro do escopo:**
- Definição de teses setoriais e importação de empresas por CNAE/CNPJ.
- Ingestão diária automática de fontes públicas (cadastrais, mercado de capitais, notícias, diário oficial).
- Classificação de texto não estruturado em registros estruturados via IA local (Ollama).
- Motor de scoring de priorização e funil kanban com drag-and-drop.
- Acesso online multiusuário com login.

**Fora do escopo (por ora):**
- Integrações com CRMs externos, disparo de e-mails/abordagens automáticas, e qualquer dado pessoal sensível além do societário público.

---

## 2. Visão geral da arquitetura

A solução é um **modular monolith**: um único projeto de deploy, com módulos internos de fronteiras bem definidas. Mantém simplicidade operacional (um servidor, um banco) e permite evoluir cada camada de forma isolada.

### Módulos (fronteiras internas)

| Módulo | Responsabilidade | Depende de |
|---|---|---|
| Ingestao | Conectores de fontes públicas; coleta bruta datada | — |
| Classificacao | Transforma texto bruto em registro estruturado via Ollama | Ingestao |
| Scoring | Calcula nota composta 0–100 por empresa | Dados, Classificacao |
| Pipeline | Estágios do funil e movimentação de cards | Dados |
| Dados | EF Core, entidades, deduplicação por CNPJ | — |
| Web | Dashboard MVC, kanban, telas de tese, login | Todos |
| Jobs | BackgroundService: orquestra o ciclo diário | Todos |

### Fluxo diário (automático)

1. Job de background dispara no horário configurado (ex.: 03:00).
2. Cada conector de Ingestao coleta itens novos desde a última execução.
3. Itens com texto livre passam pela Classificacao (Ollama) → JSON estruturado.
4. Dados cria/atualiza empresas e gatilhos, deduplicando por CNPJ.
5. Scoring recalcula a nota de cada empresa afetada.
6. Pipeline posiciona novos achados em "Identificado" e marca gatilhos de alta prioridade para revisão.

O ciclo é **idempotente**: rodar duas vezes no mesmo dia não duplica empresas nem gatilhos (chaves naturais + hash do evento).

### 2.1 Plataforma online (multiusuário, com login)

A plataforma é acessível pela internet e usada por vários membros da boutique simultaneamente. Os dados (teses, empresas, funil) são **compartilhados pela equipe** — não há base por usuário; o que varia por usuário é a identidade e o papel.

**Autenticação e autorização:**
- **ASP.NET Core Identity** (gratuito, nativo) para cadastro, login, hash de senha e gestão de sessão.
- **Papéis:** `Socio` (acesso total, vê gatilhos de alta prioridade e métricas) e `Analista` (opera o funil e o cadastro). Todas as rotas exigem autenticação; ações sensíveis exigem papel.
- **HTTPS obrigatório** em produção; cookies de sessão seguros (HttpOnly, Secure, SameSite).
- Auditoria mínima: quem moveu cada card e quando.

**Topologia de implantação (preservando o custo zero da IA):**

O ponto sensível é o Ollama: exige máquina própria (idealmente com GPU) e não roda em hospedagem compartilhada barata. Para manter a IA sem custo de API, separa-se a aplicação web do worker de IA/ingestão.

| Componente | Onde roda |
|---|---|
| Aplicação web (MVC + Identity) | Exposta na internet: VPS econômico ou serviço de app |
| Banco SQL Server | Junto da aplicação (Express/LocalDB) ou instância gerenciada |
| Worker de ingestão + Ollama | Máquina dedicada com GPU (pode ser on-premise na boutique) |

**Comunicação:** o worker escreve direto no mesmo banco (mais simples) ou expõe uma API interna protegida para a web. A IA (Ollama) **nunca é exposta à internet** — só ao worker, em rede interna.

**Alternativa de menor atrito:** se a boutique tiver um único servidor com GPU, tudo pode coabitar nessa máquina, exposta via HTTPS.

---

## 3. Estrutura de mapeamento

### 3.1 Teses setoriais (filtro primário)

Antes de qualquer dado, a boutique define de 3 a 5 teses — os setores onde tem diferencial. As teses evitam afogar a base em centenas de milhares de CNPJs irrelevantes.

| Campo da tese | Descrição |
|---|---|
| CNAEs-alvo | Lista de CNAEs que delimitam o setor |
| Faixa de faturamento | Sweet spot de fee da casa (mín./máx.) |
| Racional | Por que existem deals ali: consolidação, sucessão, pressão de margem |
| Perfil de comprador | Tipo de comprador/investidor provável |

### 3.2 Fontes de dados (todas gratuitas)

Toda fonte é pública e de acesso gratuito. O conector deve registrar origem e data de coleta de cada item.

| Fonte | O que fornece | Acesso gratuito |
|---|---|---|
| Receita / CNPJ | Razão social, CNAE, porte, sócios, situação, abertura | Dados Abertos CNPJ / APIs públicas (BrasilAPI, MinhaReceita) |
| CVM (dados abertos) | Companhias abertas, DFP/ITR, fatos relevantes | Portal de Dados Abertos da CVM (CSV/JSON) |
| Juntas / societário | Estrutura e idade de sócios | Quadro societário da base pública de CNPJ |
| Notícias | Movimentações, captações, M&A | RSS / feeds públicos de portais de economia |
| Diário Oficial | Recuperação judicial, alterações | Portais oficiais e diários públicos |

**Nota de implementação:** preferir a base de Dados Abertos do CNPJ baixada localmente (atualização periódica) para fit de tese em massa, e reservar chamadas a APIs públicas para enriquecimento pontual, respeitando limites de uso. **Validar a disponibilidade e os termos de uso de cada fonte no momento da implementação** — formatos e endpoints públicos mudam.

### 3.3 Eventos de gatilho (o coração do sistema)

A diferença entre uma lista de empresas e um pipeline é o gatilho. Cada empresa recebe flags datadas; um gatilho recente dispara a priorização.

| Gatilho | Sinal | Fonte típica |
|---|---|---|
| Sucessão | Sócios de idade avançada, sem sucessor | Receita / Juntas |
| Endividamento / estresse | RJ, default, queda de receita | Diário Oficial, CVM, notícias |
| Captação / crescimento | Rodadas, expansão, novos sócios | Notícias, CVM |
| Consolidação setorial | Concorrentes sendo adquiridos | Notícias |
| Regulatório | Mudança que pressiona margem/escala | Reguladores setoriais |

### 3.4 Camada de classificação (IA local via Ollama)

Para cada item com texto livre, uma chamada ao Ollama devolve um registro estruturado em **JSON estrito**. Como o modelo roda localmente, não há custo por chamada — o gargalo é CPU/GPU, mitigado por filtragem prévia e processamento em lote.

**Interface (contrato):** a camada de IA fica atrás de uma interface, para trocar o backend sem alterar o resto.

```csharp
public interface IClassificadorIA
{
    Task<ResultadoClassificacao> ClassificarAsync(
        string textoBruto, CancellationToken ct);
}

// Implementação default: OllamaClassificador
//  - HTTP POST http://localhost:11434/api/generate (ou /api/chat)
//  - "format": "json"  -> força saída JSON
//  - "stream": false
//  - modelo configurável (appsettings): "qwen2.5:7b" ou "llama3.1:8b"
```

**JSON de saída esperado:**

```json
{
  "empresa": "string|null",
  "cnpj": "string|null",
  "tese_sugerida": "string|null",
  "tem_gatilho": true,
  "tipo_gatilho": "Sucessao|Estresse|Captacao|Consolidacao|Regulatorio|null",
  "valor_envolvido": 0,
  "valor_estimado": false,
  "confianca": 0.0
}
```

**Regra de robustez:** o parser deve ser defensivo. Se o JSON vier malformado ou faltar campo, registrar o item como "não classificado" com o texto bruto preservado para revisão — **nunca derrubar o ciclo diário** por causa de uma saída ruim do modelo.

**Setup do Ollama (gratuito, local):**
1. Instalar Ollama no servidor/worker.
2. Baixar o modelo: `ollama pull qwen2.5:7b` (alternativa: `llama3.1:8b`).
3. GPU com ~8 GB de VRAM para latência aceitável; em CPU funciona, mais lento — aceitável no ciclo noturno em lote.
4. Serviço expõe API local em `localhost:11434`; a aplicação consome via HttpClient.

### 3.5 Scoring de priorização

Score de 0 a 100 ordena o funil, recalculado a cada informação nova. Soma ponderada de quatro componentes, todos guardados para auditabilidade.

| Componente | Peso | Como é medido |
|---|---|---|
| Fit com a tese | 40% | Match de CNAE (exato = 1,0; mesma divisão = 0,5) |
| Fit de faturamento | 20% | Dentro da faixa da tese = 1,0; decai fora dela |
| Recência do gatilho | 30% | Decaimento exponencial; ~180 dias ≈ 0,5; pondera confiança |
| Acessibilidade | 10% | Existe caminho até o decisor? |

```
score = 100 * (0.40*fitTese + 0.20*fitFat + 0.30*recencia + 0.10*acesso)

recencia = exp(-ln(2) * diasDesdeGatilho / 180) * confianca
// diasDesdeGatilho = (hoje - dataGatilho).Days
```

**Pesos configuráveis:** ficam em `appsettings` (seção `Scoring`), permitindo calibrar sem alterar código. Cada cálculo grava um snapshot dos componentes e pesos usados, para auditoria.

---

## 4. Modelo de dados

Cinco entidades centrais. **CNPJ é a chave natural de deduplicação.**

### Tese
| Campo | Tipo | Observação |
|---|---|---|
| Id | int (PK) | Identidade |
| Nome | nvarchar(120) | Ex.: "Contabilidade — sucessão" |
| CnaesAlvo | nvarchar(max) | Lista de CNAEs (CSV ou tabela filha) |
| FaturamentoMin / Max | decimal | Faixa sweet spot |
| Racional | nvarchar(max) | Por que há deals ali |
| PerfilComprador | nvarchar(max) | Comprador/investidor provável |
| Ativa | bit | Liga/desliga a tese sem apagar |

### Empresa
| Campo | Tipo | Observação |
|---|---|---|
| Id | int (PK) | Identidade |
| Cnpj | char(14) UNIQUE | Somente dígitos; chave de deduplicação |
| RazaoSocial | nvarchar(200) | |
| CnaePrincipal | char(7) | Indexado para fit de tese |
| Porte / SituacaoCadastral | nvarchar(40) | Da base da Receita |
| DataAbertura | date | Idade da empresa |
| FaturamentoEstimado | decimal null | Marcar estimativa |
| IndicadorEstimado | bit | true => exibir com "~" |
| TeseId | int FK null | Tese de melhor fit |
| FonteOrigem / DataColeta | nvarchar / datetime | Rigor de fonte |

### Gatilho
| Campo | Tipo | Observação |
|---|---|---|
| Id | int (PK) | |
| EmpresaId | int FK | N–1 Empresa |
| Tipo | nvarchar(40) | Sucessao\|Estresse\|Captacao\|Consolidacao\|Regulatorio |
| DataEvento | date | Data do fato (dirige a recência) |
| Confianca | float | 0.0–1.0 vindo da classificação |
| Fonte / Url | nvarchar | Origem citável |
| HashEvento | char(64) | Dedup de gatilho idêntico (idempotência) |
| Revisado | bit | Marcado na etapa Qualificado |

### Score
| Campo | Tipo | Observação |
|---|---|---|
| Id | int (PK) | |
| EmpresaId / TeseId | int FK | Histórico por empresa/tese |
| Valor | decimal(5,2) | 0–100 |
| CompFitTese ... CompAcesso | float | Componentes guardados p/ auditoria |
| PesosJson | nvarchar(200) | Pesos usados no cálculo (snapshot) |
| CalculadoEm | datetime | Recalculado a cada info nova |

### PipelineItem
| Campo | Tipo | Observação |
|---|---|---|
| Id | int (PK) | |
| EmpresaId | int FK UNIQUE | 1–1 Empresa |
| Estagio | nvarchar(20) | Identificado\|Qualificado\|Abordagem\|Conversa\|Mandato\|Descartado |
| Ordem | int | Posição na coluna do kanban |
| AtualizadoEm | datetime | Auditoria de movimentação |
| AtualizadoPorUsuarioId | string FK null | Quem moveu o card |

### Relações e funil
- Tese 1—N Empresa · Empresa 1—N Gatilho · Empresa 1—N Score · Empresa 1—1 PipelineItem.
- **Estágios:** Identificado → Qualificado → Abordagem → Conversa → Mandato (e Descartado). O dashboard move cards por drag-and-drop.

### Índices obrigatórios
`Empresa.Cnpj` (único), `Empresa.CnaePrincipal`, `PipelineItem.Estagio`, `Gatilho(EmpresaId, DataEvento)`.

---

## 5. Stack tecnológica (custo zero)

| Camada | Tecnologia (gratuita) |
|---|---|
| Backend | ASP.NET Core MVC (.NET 8) + Entity Framework Core |
| Autenticação | ASP.NET Core Identity: login, papéis Socio/Analista, HTTPS |
| Hospedagem | App web em VPS econômico; worker de IA em máquina própria com GPU |
| Banco | SQL Server Express / LocalDB; índices em CNPJ, CNAE, estágio e (empresa, data) |
| Classificação IA | Ollama local (qwen2.5:7b ou llama3.1:8b), saída JSON estrita, atrás de IClassificadorIA |
| Ingestão | BackgroundService (IHostedService) com ciclo diário agendado |
| Frontend | Kanban em HTML/CSS/JS (SortableJS); Tabulator.js para a visão tabular |
| Agendamento | BackgroundService nativo; Hangfire (open-source) opcional p/ painel de jobs |

Nenhum item tem custo recorrente. O único requisito de hardware acima do trivial é uma GPU para o Ollama; sem ela, o ciclo roda em CPU à noite, em lote.

---

## 6. Roadmap de implementação

Entregar valor cedo e evoluir por camadas. **Cada fase tem critério de aceitação — só avance quando a anterior estiver verde.**

### Fase 1 — MVP manual-assistido (PRIORIDADE)
- Login multiusuário (ASP.NET Core Identity, papéis Socio/Analista), HTTPS, rotas autenticadas.
- Definir teses (CRUD), importar CNPJs por CNAE, montar base e kanban funcional com drag-and-drop.
- **Aceitação:** dois usuários conseguem logar, cadastrar uma tese, importar empresas por CNAE, ver os cards no kanban e movê-los entre estágios, compartilhando a mesma base.

### Fase 2 — Camada de gatilhos
- Integrar notícias e CVM; classificação via Ollama gerando flags datadas com fonte.
- **Aceitação:** um item de notícia vira um gatilho estruturado, vinculado à empresa certa, com data e fonte citável.

### Fase 3 — Scoring automático
- Implementar o score composto e a ordenação do funil; pesos em appsettings.
- **Aceitação:** empresas aparecem ordenadas por score; alterar pesos no config muda a ordem sem recompilar.

### Fase 4 — Ingestão diária automatizada
- Job que atualiza a base diariamente e marca gatilhos de alta prioridade para revisão.
- **Aceitação:** o ciclo roda sozinho todo dia, é idempotente (não duplica) e gera lista de novos achados de alta prioridade.

---

## 7. Riscos e conformidade

| Risco | Mitigação |
|---|---|
| LGPD | Apenas fontes públicas; registrar origem de cada dado; cautela com dados de sócios |
| Exposição na internet | HTTPS obrigatório; rotas autenticadas; Ollama nunca exposto; senhas com hash via Identity |
| Qualidade de dado | Marcar estimativas com "~"; só dados confirmados e citáveis em materiais de decisão |
| Custo / capacidade de IA | IA local elimina custo por chamada; filtrar por termos antes de classificar; lote noturno |
| Falsos positivos de gatilho | Grau de confiança + revisão humana no estágio Qualificado |
| Saída malformada do modelo | Parser defensivo; item vira "não classificado" preservando texto bruto |
| Mudança/indisponibilidade de fonte | Conectores isolados atrás de interface; falha de uma não interrompe as demais |

---

## 8. Métricas de sucesso

- Número de targets qualificados gerados por mês.
- Taxa de conversão de Identificado → Mandato.
- Tempo entre gatilho e primeira abordagem.
- Pipeline ponderado por score (valor esperado da originação).

---

## 9. Primeiros passos para o Claude Code

1. Criar a solução modular monolith em .NET 8 com os módulos da seção 2.
2. Configurar ASP.NET Core Identity (login, papéis Socio/Analista) e HTTPS; proteger todas as rotas.
3. Modelar as 5 entidades da seção 4 com EF Core; gerar a primeira migration; aplicar índices obrigatórios.
4. Implementar a Fase 1 inteira até passar no critério de aceitação.
5. Só então abrir a Fase 2: stub de IClassificadorIA + OllamaClassificador com o contrato JSON da seção 3.4.

**Trabalhe uma fase por vez. Não inicie a Fase N+1 antes de a Fase N satisfazer seu critério de aceitação.**
