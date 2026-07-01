# EconomiaSim — simulação socioeconômica baseada em agentes

Protótipo de um **modelo baseado em agentes (ABM)** que simula uma sociedade
artificial (estilo Brasil) para estudar o **efeito distributivo** de mudanças de
política econômica: Selic, meta de inflação, choques de oferta etc.

Você mexe nas alavancas (taxa de juros, meta, credibilidade do BC, indexação
salarial...) e observa como **classes de renda diferentes (A–E)** reagem ao longo
do tempo — inflação (IPCA), desemprego, consumo, patrimônio e desigualdade (Gini).

## Como rodar

Pré-requisito: .NET 8 SDK (já instalado).

```bash
cd EconomiaSim
dotnet run                 # cenário: choque inflacionário + super Selic
dotnet run -- padrao       # cenário base (BC seguindo a Regra de Taylor)
dotnet run -- experimento  # laboratório de causalidade: baseline vs. tratamentos
dotnet run -- llm          # ao final, consulta agentes-LLM via Ollama (híbrido)
```

### Laboratório de causalidade (análise A/B)

`dotnet run -- experimento` é o coração da análise causal: roda um **baseline** e um
**tratamento** que muda *uma* alavanca (ceteris paribus) e imprime o diff — o que
mudou nos agregados (IPCA, Selic, desemprego, investimento, Gini) e em **cada
classe** (consumo real, desemprego), revelando quem ganha, quem perde e por qual
canal. Defina seus próprios experimentos em `RodarExperimentos()` no `Program.cs`.

As alavancas de incentivo/desincentivo ficam em `Politica` (todas neutras = 0 no
baseline; ver `Politica.cs`):

| Alavanca | Agente | Efeito |
|---|---|---|
| `IRProgressivo` | famílias | imposto de renda progressivo (mais sobre o topo) |
| `TransferenciaMensal` | famílias | renda básica / Bolsa Família à base (D/E) |
| `ImpostoConsumo` | famílias | IVA/ICMS sobre consumo (regressivo) |
| `SalarioMinimo` | famílias | piso salarial (sobe renda, desincentiva contratar) |
| `ImpostoCorporativo` | empresas | tributa lucro → menos investimento |
| `SubsidioInvestimento` | empresas | desoneração → mais investimento |
| `EncargosTrabalhistas` | empresas | custo de contratar → menos emprego |
| `InvestimentoPublico` | governo | % do PIB em infraestrutura (demanda + produtividade + saúde) |

Exemplos que leem bem: a **renda básica** reduz o Gini e dá +45% de consumo à
classe E; o **salário mínimo alto** eleva a renda da base mas aumenta o desemprego
(trade-off clássico); a **desoneração** eleva o investimento.

### Visualização interativa (cidade isométrica)

`cidade.html` é uma vitrine visual autônoma: abra com duplo-clique no navegador
(offline, sem instalar nada). Cada prédio é uma família por classe (A–E), com
ciclo dia/noite, carros e pedestres; mexa na Selic e na meta de inflação e veja
o desemprego apagar as luzes e o Gini subir. Clique numa casa para inspecionar a
família. Roda uma versão **simplificada** do modelo em JavaScript — o motor de
referência continua sendo o projeto .NET descrito abaixo.

Cada execução também exporta `bin/Debug/net8.0/resultado.csv` com a série mensal
completa (por classe), pronta para gráficos no Excel/Power BI.

## Arquitetura

```
Model/
  ClasseRenda.cs     Classes A–E: parcela da população, renda, MPC, patrimônio inicial
  Familia.cs         Agente família: trabalha, recebe/paga juros, forma expectativa,
                     decide consumo vs. poupança (efeito-renda e efeito-substituição)
  Empresa.cs         Firma individual (capacidade); o setor é uma população delas
  SetorProdutivo.cs  População de empresas: entrada/falência, produção, Phillips
  BancoCentral.cs    Regra de Taylor: define a Selic conforme inflação e hiato
  Demografia.cs      Taxas de nascimento, morte e mobilidade social por família
  Politica.cs        Alavancas de incentivo/desincentivo (fiscais e regulatórias)
  Experimento.cs     Máquina de experimento A/B (ceteris paribus)
  Cenario.cs         Parâmetros e scripts de choque (as "alavancas")
  Simulacao.cs       Motor do loop mensal + métricas distributivas (Gini etc.)
Llm/
  OllamaAgente.cs    Camada HÍBRIDA: LLM local dá leitura qualitativa por classe
Program.cs           Runner: roda o cenário, imprime relatório, exporta CSV
```

### Loop mensal (causalidade)

1. **Banco Central** define a Selic (Regra de Taylor: reage ao desvio da inflação
   vs. meta e ao hiato do produto). Pode ser sobreposta por um choque do cenário.
2. **Bancos** repassam: poupança ≈ Selic; crédito = Selic + spread.
3. **Famílias** decidem consumo/poupança. Aqui mora o efeito distributivo:
   poupadores (ricos) ganham com juros altos; endividados (pobres) perdem.
4. **Setor produtivo** apura produção, inflação (Phillips + inércia + expectativa
   + choque) e nível de preços.
5. **Mercado de trabalho**: emprego depende do hiato; demissões atingem primeiro
   as classes mais baixas (*last hired, first fired*).
6. **Expectativas** se atualizam (adaptativas + âncora na meta do BC).

## As alavancas (em `Cenario.cs`)

| Parâmetro            | O que controla                                            |
|----------------------|-----------------------------------------------------------|
| `SelicInicialAnual`  | Selic de partida                                          |
| `MetaInflacaoAnual`  | meta de inflação do BC                                    |
| `CredibilidadeBC`    | quanto as expectativas se ancoram na meta (0..1)          |
| `SpreadBancarioAnual`| diferença entre juro de crédito e Selic                   |
| `IndexacaoSalarial`  | fração da inflação repassada aos salários                 |
| `Choques`            | script de choques de oferta e Selic forçada por mês       |

Crie cenários novos como métodos estáticos em `Cenario.cs` (veja
`ChoqueESuperSelic()` como exemplo).

## O que o protótipo já mostra

No cenário de choque, a riqueza real da **classe A cresce centenas de %** enquanto
a **classe E afunda na dívida**; o **Gini sobe de ~0,49 para ~0,71**; e o
**desemprego se concentra nas classes baixas** (E em 17–35%, A em 0%). Ou seja: o
combate à inflação via juros altos tem um custo distributivo regressivo.

## Sociedade viva — demografia (Fase 1)

A população **não é fixa**: cada família tem, por mês, probabilidades de
**nascimento** (formar novo domicílio), **morte** (dissolução) e **mobilidade
social** (subir/descer de classe), todas em `Demografia.cs` e ligadas ao estado
econômico — emprego, prosperidade real (renda/renda-de-referência da classe) e
inflação. Mais capacidade produtiva acompanha (parcialmente) a população.

Resultado: a causalidade "variável → a sociedade cresce ou definha" fica
**mensurável**. No laboratório A/B, a **renda básica** e o **IR progressivo +
transferência** fazem a população crescer (prosperidade na base → natalidade↑,
mortalidade↓); o **salário mínimo alto** a encolhe (desemprego↑).

## Sociedade viva — empresas (Fase 2)

O setor produtivo é uma **população de empresas** (`Empresa.cs`, `SetorProdutivo.cs`):
firmas **nascem** quando há lucro (utilização alta, crédito barato, imposto baixo,
subsídio) e **fecham** na crise. Para manter o macro estável, a capacidade agregada
segue um caminho suave (investimento) e as firmas a dividem — quando uma fecha, as
sobreviventes absorvem o mercado. Assim a *contagem* de empresas é a camada viva.
No laboratório A/B, a **desoneração ao investimento** faz as firmas crescerem
(89→130); o **salário mínimo alto** as reduz; o imposto sobre consumo as derruba
(via o trap de inflação documentado).

O visual `cidade-viva-empresas.html` reflete a Fase 2: prédios comerciais
(azul-aço) nascem e são demolidos junto das casas, com 6 alavancas e contadores
de domicílios e empresas.

## Sociedade viva — governo e infraestrutura (Fase 3)

A alavanca `InvestimentoPublico` (% do PIB) faz o Estado construir um estoque de
**infraestrutura** (índice que deprecia), com efeito triplo: é **demanda** hoje
(obras empregam), eleva a **produtividade** (mesma capacidade produz mais) e
melhora a **saúde** (reduz a mortalidade na demografia). No laboratório A/B,
investir 5% do PIB constrói infra (1,00→1,13), reduz o desemprego e aumenta a
população — mas aquece a demanda, sobe a Selic e faz algum **crowding-out** das
firmas privadas (menos empresas). Próximo passo: integrar o motor .NET aos visuais
(hoje os visuais rodam um modelo simplificado em paralelo).

## Canal de investimento (transmissão da política monetária)

O `SetorProdutivo` modela o investimento das empresas: ele é (a) parte da demanda
agregada hoje, (b) construtor de capacidade amanhã, e (c) **cai quando o juro real
do crédito sobe**. É por aqui que a Selic alta esfria a economia — não só via
consumo. Com isso, no cenário base a política monetária funciona: a inflação
oscila em torno da meta (ciclo amortecido) em vez de travar em patamar alto, e o
investimento sobe/desce conforme a Selic (~18% no crédito caro, ~22% no barato).

## Limitações conhecidas (próxima fase)

Este é um **modelo didático**, não calibrado a dados reais ainda. Pontos a evoluir:

- **Armadilha de inflação alta (segundo atrator).** O sistema tem um "atrator ruim"
  — Selic no teto + inflação alta + Gini disparado. Choques fortes podem empurrar a
  economia do regime saudável para ele. É o que acontece com o **imposto sobre
  consumo de 20%**: a *distribuição* sai correta (regressiva — D/E perdem mais), mas
  o *macro* cai na armadilha (Selic ~34-50% em vez de desinflação limpa). Atacar isso
  é o próximo passo real: enfraquecer o canal de renda de juros e/ou suavizar a
  dinâmica de capacidade para eliminar o segundo atrator.
- **Setor produtivo agregado.** Virar empresas individuais (com preços, estoques e
  decisões heterogêneas) traria dinâmica de mercado mais rica.
- **Sem calibração a séries reais** do BCB (SGS) e IBGE.

## Roadmap sugerido

1. **Calibrar com dados reais** (API SGS do BCB para Selic/IPCA; IBGE para renda/
   desemprego por classe) e validar contra histórico.
2. **Canal de investimento e crédito** para a política monetária ter o efeito certo.
3. **Dashboard interativo** (Blazor/ASP.NET) com sliders de Selic/meta e gráficos
   ao vivo das séries e do Gini.
4. **Camada híbrida ampliada**: agentes-LLM (Ollama) representando cada classe,
   cujo "humor" realimenta a propensão a consumir do núcleo numérico.
5. **Política fiscal**: governo, impostos, transferências (Bolsa Família) — peça
   central da distribuição.
```
