var values = new[] { 1, 2, 3 };
var positive = values.Where(value => value > 0).ToArray();
Console.WriteLine(positive.Length > 0 ? positive.Length : 0);
