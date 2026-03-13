using Birko.Data.SQL.Attributes;

namespace Birko.Models
{
    public abstract class AbstractDatabasePercentage : AbstractPercentage
    {
        [PrecisionField(ValueData.StoreDecimalPrecision)]
        [ScaleField(ValueData.StoreDecimalPlaces)]
        public override decimal Percentage { get; set; } = 0;
    }
}
