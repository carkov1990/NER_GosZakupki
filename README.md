# NER_GosZakupki
**Извлечение именованных сущностей для гос. контрактов**

# Задача:
Из судебных актов выделить Участников, реквизиты, номера контрактов, и даты контрактов. С помощью этих данных попытаться связать дело и контракт из БД GosZakupki.

# Реализация:
Задача по своей сути состоит из 2х подзадач:
1. Выделение данных из текста 
2. Поиск и связывание акта(дела) с контрактами. 

Первая часть является задачей извлечения именованных сущностей, которая была выполнена в компании раньше с помощью регулярных выражений и Tommita парсера. 
Перед реализацией постановки был проведен анализ дополнительных средств позволяющих извлекать ИС. Для тестового использования был выбран Pullenti (http://www.pullenti.ru/Default.aspx)

Данный инструмент имеет в своем наборе стандартные анализацторы, которые помогают извлекать организации и их реквизиты, даты и номера. Суть работы pullenti состоит в том, что он разбивает текст на токены,
затем он проводит анализ каждого токена и присваивает им какие то свойства - род, число, есть ли пробел за токеном и т.п. На основе токенов могут быть построены метатокены, а после могут быть выделены необходимые сущности.

Для реализации **первой части** был применен следующий подход
Был создан анализатор выделения собственных Referent, которые содержат в себе список контрактов с возможными датами и участники с реквизитами.

```csharp
public class MyReferent : Referent
{
	public MyReferent(string typ) : base(typ)
	{
	}

	public List<Participant> Participants { get; set; }

	public List<Contract> Contracts { get; set; }
}
```

```csharp
public class Participant
{
	public string Name { get; set; }

	public string Inn { get; set; }

	public string Ogrn { get; set; }
}
```

```csharp
public class Contract
{
	public string Number { get; set; }

	public List<DateTime?> Dates { get; set; } = new List<DateTime?>();
}
```

Анализатор получая текст вызывает стандартные механизмы выделения организаций. После получения списка сущностей запускается цикл поиска реквизитов.  

Реквизиты ищутся в пределах окрестностей символов, откуда была выделена сущность. Если реквизиты найдены, то происходит переход к другой сущности, 
если нет, то переход к другому месту выделения сущности. Полученные участники проверяеются на дубликаты с помощью OrganizationComparator и сохраняются в MyReferent.

После выделения организаций происходит выделение всех дат из текста. Рядом с выделенными датами ищутся любые номера. Номер выделяется с помощью встроенной процедуры 
MiscHelper.CheckNumberPrefix. Выделенные данные сохраняются как номера с датами. Затем из текста пытаемся выделить номера идущие после слова "КОНТРАКТ". 

Эти номера больше подходят для решения поставленной задачи, но так как иногда суды пишут краткий номер а не полный регистранционный номер, нам необходимо получить и даты по выделенным контрактам.

Это делается выделением пересекающихся номеров контрактов из списка по датам и списка по слову "КОНТРАКТ". Данное пересечение и добавлеяется в результирующий референт.
Дальше выделяются просто номера (как мусорные данные), но применения пока им не нашлось

**Вторая часть** задачи состоит в том, чтобы получив референт найти по выделенным участникам и номерам контрактов нужный контракт.

В цикле по участникам мы делаем запросы, где текущий участник заказчик, а все остальные поставщики. Если контракт 1, то это скорее всего наш, но тут,
лучше бы проверить дополнительно по датам и номерам. Если номера не подходят, то это не беда, а вот если даты не подходят, то это скорее всего не нащ контракт.
Так же может быть вариант, что контрактов между нашими участниками больше чем один, тогда тут идет дофильтрация по номерам и датам. Но и эта фильтрация может вернуть больше 1 контракта. К сожалению данный вариант пока никак не обработан. 

**Работа программы**
Программа представляет из себя консольное приложение, которое работает в 2х режимах выбираемых 1м аргументом запуска.

**1 режим = аргумент txt**
При данном режиме в папке Texts должны находиться *.txt файлы с текстом актов, которые необходимо распарсить. Программа по очереди берет файлы и обрабатывает их.
Результат программы сохраняется в папке Results, где для каждого файла из Texts есть свой одноименный файл. В результирующем файле есть следующие пункты
 * Организации
 * Контракты после слова 'контракт'
 * Контракты рядом с датами
 * Скорее всего нужные нам контракты (номера с датами входящие в номера по слову 'Контракт')
 * Мусор
 * Могли бы привязаться к следующим ID контрактов в GosZakupki

 **2й режим - csv**
Работает как первый режим, но с файлами типа *.csv
В csv файле должна быть последовательность типа
<номер дела(A56-15585/...)>;<Идентификатор дела>;<Идентификатор документа>
Текст дела получается из микросервиса ArbitrCaseCard.
Результат сохраняется в файлах папки Results и имеет идентичные нормализованные имена дел
