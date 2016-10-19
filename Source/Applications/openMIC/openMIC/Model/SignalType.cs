using System.ComponentModel.DataAnnotations;
using GSF.Data.Model;

namespace openMIC.Model
{
    public class SignalType
    {
        [PrimaryKey(true)]
        public int ID
        {
            get;
            set;
        }

        [Required]
        [StringLength(200)]
        [Searchable]
        public string Name
        {
            get;
            set;
        }

        [Required]
        [StringLength(4)]
        [Searchable]
        public string Acronym
        {
            get;
            set;
        }

        [Required]
        [StringLength(2)]
        public string Suffix
        {
            get;
            set;
        }

        [Required]
        [StringLength(2)]
        public string Abbreviation
        {
            get;
            set;
        }

        [Required]
        [StringLength(200)]
        [Searchable]
        public string LongAcronym
        {
            get;
            set;
        }

        [Required]
        [StringLength(10)]
        public string Source
        {
            get;
            set;
        }

        [StringLength(10)]
        public string EngineeringUnits
        {
            get;
            set;
        }
    }
}